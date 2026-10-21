using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// DAVE end-to-end encryption for one call, the voice gateway's side of it as libdave's own
/// reference manager does it (libdave samples/typescript/DaveSessionManager.ts): an MLS group
/// per call agreed through voice gateway ops 21-31, then a key ratchet per member that keys
/// this client's frame encryptor and each speaker's decryptor. Only members the voice gateway
/// announced (op 11) are accepted into the group. Keys live in memory for the call only.
/// </summary>
internal sealed unsafe class DaveSession : IDisposable
{
    private const ushort InitTransition = 0;
    private const ushort Disabled = 0;

    private readonly string _self;
    private readonly ulong _group;
    private readonly Action<byte, byte[]> _sendBinary;
    private readonly Action<int, object> _sendJson;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _users = [];
    private readonly Dictionary<ushort, ushort> _transitions = [];
    private readonly Dictionary<string, nint> _decryptors = [];
    private readonly Dave.FailureCallback _onFailure;
    private nint _session;
    private nint _encryptor;
    private nint _selfRatchet;
    private readonly Dictionary<string, nint> _ratchets = [];
    private ushort _latestVersion;

    internal DaveSession(string selfUserId, ulong groupId, Action<byte, byte[]> sendBinary, Action<int, object> sendJson)
    {
        _self = selfUserId;
        _group = groupId;
        _sendBinary = sendBinary;
        _sendJson = sendJson;
        _onFailure = (source, reason, _) =>
            AppLog.Info($"Discord voice: DAVE {Marshal.PtrToStringUTF8(source)}: {Marshal.PtrToStringUTF8(reason)}");
        _session = Dave.SessionCreate(0, 0, Marshal.GetFunctionPointerForDelegate(_onFailure), 0);
        _encryptor = Dave.EncryptorCreate();
        Dave.EncryptorSetPassthrough(_encryptor, true);
    }

    internal static ushort MaxVersion
    {
        get
        {
            try { return Dave.MaxProtocolVersion(); }
            catch (DllNotFoundException) { return 0; }
        }
    }

    internal bool Active => _latestVersion != Disabled;

    internal void AssignSsrc(uint ssrc, int codec = Dave.CodecOpus)
    {
        lock (_gate) Dave.EncryptorAssignSsrc(_encryptor, ssrc, codec);
    }

    internal void AddUser(string userId)
    {
        lock (_gate)
        {
            _users.Add(userId);
            SetupRatchet(userId, _latestVersion);
        }
    }

    internal void RemoveUser(string userId)
    {
        lock (_gate)
        {
            _users.Remove(userId);
            if (_decryptors.Remove(userId, out var decryptor)) Dave.DecryptorDestroy(decryptor);
            if (_ratchets.Remove(userId, out var ratchet)) Dave.KeyRatchetDestroy(ratchet);
        }
    }

    // ---- voice gateway ----

    /// <summary>op 4: the call's starting protocol version.</summary>
    internal void OnSessionDescription(ushort version)
    {
        lock (_gate) Init(version);
    }

    /// <summary>op 21: a downgrade (or reinit when the id is 0) is coming.</summary>
    internal void OnPrepareTransition(ushort transitionId, ushort version)
    {
        lock (_gate)
        {
            PrepareRatchets(transitionId, version);
            ReadyFor(transitionId);
        }
    }

    /// <summary>op 22.</summary>
    internal void OnExecuteTransition(ushort transitionId)
    {
        lock (_gate) Execute(transitionId);
    }

    /// <summary>op 24: epoch 1 is a new group.</summary>
    internal void OnPrepareEpoch(ulong epoch, ushort version)
    {
        lock (_gate)
        {
            if (epoch != 1) return;
            Dave.SessionInit(_session, version, _group, _self);
            SendKeyPackage();
        }
    }

    /// <summary>op 25 (payload after the opcode).</summary>
    internal void OnExternalSender(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
            fixed (byte* p = payload) Dave.SessionSetExternalSender(_session, p, (nuint)payload.Length);
    }

    /// <summary>op 27 (payload after the opcode: operation type, then the proposals).</summary>
    internal void OnProposals(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            byte[] commit;
            using (var ids = new UserIds(_users.Append(_self)))
            fixed (byte* p = payload)
            {
                Dave.SessionProcessProposals(_session, p, (nuint)payload.Length, ids.Pointer, ids.Count, out var data, out var length);
                commit = Dave.Take(data, length);
            }
            if (commit.Length > 0) _sendBinary(28, commit);
        }
    }

    /// <summary>op 29 (transition id, then the commit).</summary>
    internal void OnAnnounceCommit(ushort transitionId, ReadOnlySpan<byte> commit)
    {
        lock (_gate)
        {
            nint result;
            fixed (byte* p = commit) result = Dave.SessionProcessCommit(_session, p, (nuint)commit.Length);
            try
            {
                if (Dave.CommitIgnored(result)) return;
                if (!Dave.CommitFailed(result))
                {
                    PrepareRatchets(transitionId, Dave.SessionGetProtocolVersion(_session));
                    ReadyFor(transitionId);
                }
                else
                {
                    _sendJson(31, new { transition_id = transitionId });
                    Init(Dave.SessionGetProtocolVersion(_session));
                }
            }
            finally { Dave.CommitDestroy(result); }
        }
    }

    /// <summary>op 30 (transition id, then the welcome).</summary>
    internal void OnWelcome(ushort transitionId, ReadOnlySpan<byte> welcome)
    {
        lock (_gate)
        {
            nint result;
            using (var ids = new UserIds(_users.Append(_self)))
            fixed (byte* p = welcome)
                result = Dave.SessionProcessWelcome(_session, p, (nuint)welcome.Length, ids.Pointer, ids.Count);
            if (result != 0)
            {
                Dave.WelcomeDestroy(result);
                PrepareRatchets(transitionId, Dave.SessionGetProtocolVersion(_session));
                ReadyFor(transitionId);
            }
            else
            {
                _sendJson(31, new { transition_id = transitionId });
                SendKeyPackage();
            }
        }
    }

    // ---- media ----

    /// <summary>This client's Opus (or, with video, H.264) frame, end-to-end encrypted (or as is while the call is not E2EE).</summary>
    internal int EncryptFrame(uint ssrc, ReadOnlySpan<byte> frame, Span<byte> output, bool video = false)
    {
        lock (_gate)
        {
            fixed (byte* f = frame)
            fixed (byte* o = output)
            {
                var code = Dave.Encrypt(_encryptor, video ? Dave.MediaVideo : Dave.MediaAudio, ssrc, f, (nuint)frame.Length, o, (nuint)output.Length, out var written);
                return code == 0 ? (int)written : -1;
            }
        }
    }

    /// <summary>A speaker's frame decrypted; -1 when it can't be (no key yet, or not for this group).</summary>
    internal int DecryptFrame(string userId, ReadOnlySpan<byte> frame, Span<byte> output, bool video = false)
    {
        lock (_gate)
        {
            if (!_decryptors.TryGetValue(userId, out var decryptor))
            {
                if (!Active) { frame.CopyTo(output); return frame.Length; }
                return -1;
            }
            fixed (byte* f = frame)
            fixed (byte* o = output)
            {
                var code = Dave.Decrypt(decryptor, video ? Dave.MediaVideo : Dave.MediaAudio, f, (nuint)frame.Length, o, (nuint)output.Length, out var written);
                return code == 0 ? (int)written : -1;
            }
        }
    }

    // ---- internals (under _gate) ----

    private void Init(ushort version)
    {
        if (version > 0)
        {
            Dave.SessionInit(_session, version, _group, _self);
            SendKeyPackage();
        }
        else
        {
            PrepareRatchets(InitTransition, version);
            Execute(InitTransition);
        }
    }

    private void Execute(ushort transitionId)
    {
        if (!_transitions.Remove(transitionId, out var version)) return;
        if (version == Disabled) Dave.SessionReset(_session);
        SetupRatchet(_self, version);
    }

    private void PrepareRatchets(ushort transitionId, ushort version)
    {
        foreach (var user in _users) SetupRatchet(user, version);
        if (transitionId == InitTransition) SetupRatchet(_self, version);
        else _transitions[transitionId] = version;
        _latestVersion = version;
    }

    private void SetupRatchet(string userId, ushort version)
    {
        var ratchet = version == Disabled ? 0 : Dave.SessionGetKeyRatchet(_session, userId);
        if (userId == _self)
        {
            if (_selfRatchet != 0) Dave.KeyRatchetDestroy(_selfRatchet);
            _selfRatchet = ratchet;
            Dave.EncryptorSetPassthrough(_encryptor, ratchet == 0);
            if (ratchet != 0) Dave.EncryptorSetKeyRatchet(_encryptor, ratchet);
            return;
        }
        if (!_decryptors.TryGetValue(userId, out var decryptor)) _decryptors[userId] = decryptor = Dave.DecryptorCreate();
        if (_ratchets.Remove(userId, out var old)) Dave.KeyRatchetDestroy(old);
        if (ratchet == 0) Dave.DecryptorSetPassthrough(decryptor, true);
        else
        {
            _ratchets[userId] = ratchet;
            Dave.DecryptorSetKeyRatchet(decryptor, ratchet);
        }
    }

    private void ReadyFor(ushort transitionId)
    {
        if (transitionId != InitTransition) _sendJson(23, new { transition_id = transitionId });
    }

    private void SendKeyPackage()
    {
        Dave.SessionGetKeyPackage(_session, out var data, out var length);
        var package = Dave.Take(data, length);
        if (package.Length > 0) _sendBinary(26, package);
    }

    // The members libdave may accept (everyone the voice gateway announced, and this user), as C strings.
    private sealed class UserIds : IDisposable
    {
        private readonly nint[] _strings;
        private readonly nint _array;

        internal UserIds(IEnumerable<string> ids)
        {
            _strings = [.. ids.Select(Marshal.StringToCoTaskMemUTF8)];
            _array = Marshal.AllocCoTaskMem(Math.Max(1, _strings.Length) * IntPtr.Size);
            Marshal.Copy(_strings, 0, _array, _strings.Length);
        }

        internal nint* Pointer => (nint*)_array;
        internal nuint Count => (nuint)_strings.Length;

        public void Dispose()
        {
            foreach (var p in _strings) Marshal.FreeCoTaskMem(p);
            Marshal.FreeCoTaskMem(_array);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var decryptor in _decryptors.Values) Dave.DecryptorDestroy(decryptor);
            _decryptors.Clear();
            foreach (var ratchet in _ratchets.Values) Dave.KeyRatchetDestroy(ratchet);
            _ratchets.Clear();
            if (_selfRatchet != 0) { Dave.KeyRatchetDestroy(_selfRatchet); _selfRatchet = 0; }
            if (_encryptor != 0) { Dave.EncryptorDestroy(_encryptor); _encryptor = 0; }
            if (_session != 0) { Dave.SessionDestroy(_session); _session = 0; }
        }
    }
}
