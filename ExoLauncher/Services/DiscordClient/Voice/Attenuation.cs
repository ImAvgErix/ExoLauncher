using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Discord's Global Attenuation: while someone speaks in the call (others, or you, as the user chose),
/// every other app's volume on the call's speakers is lowered by the chosen percent, the way the
/// Windows volume mixer would, and put back when the talking stops or the call ends. A volume the
/// user changed in the meantime is left as they set it. Exo's own sound is never lowered.
/// </summary>
internal sealed class Attenuation : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (ISimpleAudioVolume Volume, float Original, float Lowered)> _lowered = [];
    private bool _want;

    /// <summary>Who is speaking now; applies or lifts the attenuation off the calling thread.</summary>
    internal void Update(bool selfSpeaking, bool othersSpeaking)
    {
        var s = App.Services.Settings.Shared;
        var want = s.DiscordAttenuation > 0 && ((s.DiscordAttenuateSelf && selfSpeaking) || (s.DiscordAttenuateOthers && othersSpeaking));
        lock (_gate)
        {
            if (want == _want) return;
            _want = want;
        }
        ThreadPool.QueueUserWorkItem(_ => Apply());
    }

    private void Apply()
    {
        lock (_gate)
        {
            try
            {
                if (_want && _lowered.Count == 0) Lower(1f - App.Services.Settings.Shared.DiscordAttenuation / 100f);
                else if (!_want) Restore();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                AppLog.Info($"Discord voice: attenuation failed (0x{ex.HResult:X8}).");
            }
        }
    }

    // Under _gate.
    private void Lower(float factor)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (AudioDevices.Open(enumerator, capture: false, App.Services.Settings.Shared.DiscordOutputDevice) is not { } device) return;
        var iid = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref iid, ClsctxAll, 0, out var managerObject) != 0) return;
        var manager = (IAudioSessionManager2)managerObject;
        if (manager.GetSessionEnumerator(out var sessions) != 0) return;
        sessions.GetCount(out var count);
        var self = (uint)Environment.ProcessId;
        for (var i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var control) != 0 || control is not IAudioSessionControl2 session) continue;
            if (session.GetProcessId(out var pid) != 0 || pid == self || pid == 0 || session.IsSystemSoundsSession() == 0) continue;
            if (session.GetSessionInstanceIdentifier(out var id) != 0 || control is not ISimpleAudioVolume volume) continue;
            if (volume.GetMasterVolume(out var original) != 0) continue;
            var lowered = original * factor;
            if (volume.SetMasterVolume(lowered, ref EventContext) == 0) _lowered[id] = (volume, original, lowered);
        }
    }

    // Under _gate.
    private void Restore()
    {
        foreach (var (volume, original, lowered) in _lowered.Values)
        {
            try
            {
                if (volume.GetMasterVolume(out var now) == 0 && Math.Abs(now - lowered) < 0.01f) volume.SetMasterVolume(original, ref EventContext);
            }
            catch (COMException) { }
        }
        _lowered.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _want = false;
            try { Restore(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        }
    }
}
