using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's DevTools connection to the Spotify it launched, over a private pipe
/// (<c>--remote-debugging-pipe</c>) rather than a loopback port. Only Exo holds
/// the pipe's ends, so no other program on the PC can find or drive Spotify's
/// signed-in page; a port, even a random one, is readable from the command line
/// (security/attacks/local, attack 1). Spotify's Chromium reads the pipe on file
/// descriptors 3 and 4, handed over the way the C runtime inherits them
/// (STARTUPINFO.lpReserved2), and those two are the only handles it inherits.
/// One connection serves every caller; each attaches its own flat session to a
/// page target. The connection closes when that Spotify process exits, so a
/// Spotify that restarted itself is known to be out of reach.
/// </summary>
internal static class SpotifyDevTools
{
    internal const string PipeArgument = "--remote-debugging-pipe";
    private const int MaxMessageBytes = 16 << 20;
    private static Connection? _current;

    internal static bool IsConnected => Volatile.Read(ref _current) is { IsClosed: false };

    /// Starts Spotify with the pipe and makes it the current connection. Returns its pid.
    internal static int Launch(string executable, string arguments, string workingDirectory)
    {
        var toChild = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChild = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            var pid = StartWithPipe(executable, arguments, workingDirectory,
                toChild.ClientSafePipeHandle.DangerousGetHandle(), fromChild.ClientSafePipeHandle.DangerousGetHandle());
            toChild.DisposeLocalCopyOfClientHandle();
            fromChild.DisposeLocalCopyOfClientHandle();
            var connection = new Connection(toChild, fromChild);
            Interlocked.Exchange(ref _current, connection)?.Close();
            connection.Start();
            return pid;
        }
        catch
        {
            toChild.Dispose();
            fromChild.Dispose();
            throw;
        }
    }

    /// One DevTools command; the whole reply (<c>result</c> or <c>error</c>), or null
    /// when there is no connection or it closed before answering.
    internal static async Task<JsonElement?> SendAsync(string method, object? parameters, string? sessionId,
        CancellationToken cancellation)
    {
        if (Volatile.Read(ref _current) is not { IsClosed: false } connection) return null;
        return await connection.SendAsync(method, parameters, sessionId, cancellation).ConfigureAwait(false);
    }

    /// The page targets Spotify has open: id and URL.
    internal static async Task<IReadOnlyList<(string Id, string Url)>> PagesAsync(CancellationToken cancellation)
    {
        var reply = await SendAsync("Target.getTargets", null, null, cancellation).ConfigureAwait(false);
        var pages = new List<(string, string)>();
        if (reply is not { } message || !message.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return pages;
        foreach (var info in infos.EnumerateArray())
        {
            if (info.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                info.TryGetProperty("targetId", out var id) && id.GetString() is { Length: > 0 } targetId)
                pages.Add((targetId, info.TryGetProperty("url", out var url) ? url.GetString() ?? "" : ""));
        }
        return pages;
    }

    /// Attaches a flat session to one page; its id, or null.
    internal static async Task<string?> AttachAsync(string targetId, CancellationToken cancellation)
    {
        var reply = await SendAsync("Target.attachToTarget", new { targetId, flatten = true }, null, cancellation).ConfigureAwait(false);
        var id = reply is { } message && message.TryGetProperty("result", out var result) &&
                 result.TryGetProperty("sessionId", out var session) ? session.GetString() : null;
        // The id belongs to this connection: after Spotify restarts, a new connection must not take the old one's id as its own.
        if (id is not null && Volatile.Read(ref _current) is { } current) current.Track(id);
        return id;
    }

    /// False once the page closed, the session was detached, or Spotify exited.
    internal static bool IsLive(string sessionId) =>
        Volatile.Read(ref _current) is { IsClosed: false } connection && connection.IsLive(sessionId);

    /// The C runtime's inherited-descriptor block: a count, one flag byte per
    /// descriptor, then one handle per descriptor. 0-2 are closed; 3 and 4 are
    /// the pipe (FOPEN | FPIPE), as Puppeteer and libuv pass them.
    internal static byte[] CrtDescriptorBlock(nint childRead, nint childWrite)
    {
        const int count = 5;
        var block = new byte[4 + count + count * IntPtr.Size];
        BitConverter.TryWriteBytes(block.AsSpan(0, 4), count);
        block[4 + 3] = 0x09;
        block[4 + 4] = 0x09;
        var handles = new[] { (nint)(-1), -1, -1, childRead, childWrite };
        for (var i = 0; i < count; i++)
            BitConverter.TryWriteBytes(block.AsSpan(4 + count + i * IntPtr.Size, IntPtr.Size), (long)handles[i]);
        return block;
    }

    private static int StartWithPipe(string executable, string arguments, string workingDirectory, nint childRead, nint childWrite)
    {
        var block = CrtDescriptorBlock(childRead, childWrite);
        var reserved = Marshal.AllocHGlobal(block.Length);
        var handleList = Marshal.AllocHGlobal(2 * IntPtr.Size);
        nint attributes = 0;
        try
        {
            Marshal.Copy(block, 0, reserved, block.Length);
            Marshal.WriteIntPtr(handleList, 0, childRead);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, childWrite);
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            try
            {
                // Only the pipe's two ends are inherited, never another handle Exo holds.
                if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList,
                        2 * IntPtr.Size, 0, 0))
                    throw new Win32Exception();
                var info = new StartupInfoEx { AttributeList = attributes };
                info.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                // No standard handles: a Chromium client's console.log into a dead pipe
                // dies with an EPIPE dialog (why Exo otherwise uses ShellExecute).
                info.StartupInfo.dwFlags = StartfUseStdHandles;
                info.StartupInfo.cbReserved2 = (short)block.Length;
                info.StartupInfo.lpReserved2 = reserved;
                var commandLine = new StringBuilder("\"" + executable + "\" " + arguments);
                if (!CreateProcessW(null, commandLine, 0, 0, true, ExtendedStartupInfoPresent, 0,
                        workingDirectory, ref info, out var process))
                    throw new Win32Exception();
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
                return process.dwProcessId;
            }
            finally { DeleteProcThreadAttributeList(attributes); }
        }
        finally
        {
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(reserved);
        }
    }

    private sealed class Connection(AnonymousPipeServerStream toChild, AnonymousPipeServerStream fromChild)
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
        private readonly ConcurrentDictionary<string, byte> _detached = new();
        private readonly ConcurrentDictionary<string, byte> _attached = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _nextId;
        private volatile bool _closed;

        internal bool IsClosed => _closed;
        internal bool IsLive(string sessionId) => !_closed && _attached.ContainsKey(sessionId) && !_detached.ContainsKey(sessionId);
        internal void Track(string sessionId) => _attached[sessionId] = 0;

        internal void Start() => _ = Task.Run(ReadLoopAsync);

        internal async Task<JsonElement?> SendAsync(string method, object? parameters, string? sessionId, CancellationToken cancellation)
        {
            var id = Interlocked.Increment(ref _nextId);
            var reply = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            try
            {
                var message = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
                if (parameters is not null) message["params"] = parameters;
                if (sessionId is not null) message["sessionId"] = sessionId;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
                await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (_closed) return null;
                    await toChild.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                    toChild.WriteByte(0);
                    await toChild.FlushAsync(cancellation).ConfigureAwait(false);
                }
                catch (IOException) { Close(); return null; }
                finally { _writeGate.Release(); }
                return await reply.Task.WaitAsync(cancellation).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                int read;
                while ((read = await fromChild.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    var start = 0;
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] != 0) continue;
                        message.Write(buffer, start, i - start);
                        start = i + 1;
                        Dispatch(message);
                        message.SetLength(0);
                    }
                    message.Write(buffer, start, read - start);
                    if (message.Length > MaxMessageBytes) break;
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            finally { Close(); }
        }

        private void Dispatch(MemoryStream message)
        {
            try
            {
                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = json.RootElement;
                if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var key))
                {
                    if (_pending.TryGetValue(key, out var reply)) reply.TrySetResult(root.Clone());
                }
                else if (root.TryGetProperty("method", out var method) && method.GetString() == "Target.detachedFromTarget" &&
                         root.TryGetProperty("params", out var parameters) &&
                         parameters.TryGetProperty("sessionId", out var session) && session.GetString() is { } detached)
                {
                    _detached[detached] = 0;
                }
            }
            catch (JsonException) { /* one unreadable message; the rest still arrive */ }
        }

        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            foreach (var reply in _pending.Values) reply.TrySetResult(null);
            try { toChild.Dispose(); } catch { }
            try { fromChild.Dispose(); } catch { }
        }
    }

    private const int ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly nint ProcThreadAttributeHandleList = 0x00020002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, int creationFlags, nint environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint attributeList, int flags, nint attribute, nint value,
        nint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
