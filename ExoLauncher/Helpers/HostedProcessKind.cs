using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

internal enum HostedProcessKind
{
    Host, Gpu, Renderer, Audio, Network, Cdm, Crashpad, Extension, Utility, Unknown
}

/// ShellHidden: Exo's own window is minimized or hidden, so nothing of its
/// host or shell WebView is on screen. LiveAudio: a call or other live sound
/// runs in the host, which then keeps its working set.
internal readonly record struct HostedTrimPolicy(
    bool MusicRoomOpen, bool MusicPlaying, bool ShellHidden = false, bool LiveAudio = false);

/// <summary>
/// Classifies a hosted Spotify process from its
/// command line, and decides which of those processes can lose their working
/// set without stopping a song. Pure over the command line; the
/// query helper is the only Win32.
/// </summary>
internal static class HostedProcessClass
{
    /// Idle freeze wait after the room is closed and nothing is playing.
    /// Mini player skip/play and reopening the room thaw first.
    internal static readonly TimeSpan IdleFreezeDelay = TimeSpan.FromSeconds(30);

    internal static HostedProcessKind FromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return HostedProcessKind.Unknown;
        if (ContainsType(commandLine, "gpu-process")) return HostedProcessKind.Gpu;
        if (ContainsType(commandLine, "crashpad-handler")) return HostedProcessKind.Crashpad;
        if (ContainsType(commandLine, "renderer"))
            return commandLine.Contains("--extension-process", StringComparison.OrdinalIgnoreCase)
                ? HostedProcessKind.Extension
                : HostedProcessKind.Renderer;
        if (ContainsType(commandLine, "utility"))
        {
            if (commandLine.Contains("audio.mojom", StringComparison.OrdinalIgnoreCase))
                return HostedProcessKind.Audio;
            if (commandLine.Contains("network.mojom", StringComparison.OrdinalIgnoreCase))
                return HostedProcessKind.Network;
            if (commandLine.Contains("CdmService", StringComparison.OrdinalIgnoreCase))
                return HostedProcessKind.Cdm;
            return HostedProcessKind.Utility;
        }
        return commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase)
            ? HostedProcessKind.Unknown
            : HostedProcessKind.Host;
    }

    /// Crashpad never paints or plays. An unused extension renderer is the
    /// same. A generic utility (storage, not audio/network/CDM) can go too.
    internal static bool TrimWhileRoomOpen(HostedProcessKind kind) =>
        kind is HostedProcessKind.Crashpad or HostedProcessKind.Extension;

    /// Room closed, but a song is live. Keep the decoder, audio graph, DRM,
    /// network and the host; the GPU process goes.
    /// Spotify decodes and plays in its host process, so its renderer only
    /// draws the hidden UI and goes too.
    internal static bool TrimWhileLiveHidden(HostedProcessKind kind) =>
        kind is HostedProcessKind.Crashpad or HostedProcessKind.Extension or HostedProcessKind.Utility or HostedProcessKind.Gpu
            or HostedProcessKind.Renderer;

    /// Room closed and silent: every process can release pages. Playback and
    /// skip thaw the tree before SMTC runs.
    internal static bool TrimWhileIdleHidden() => true;

    internal static bool ShouldTrim(HostedProcessKind kind, bool roomOpen, bool liveMedia)
    {
        if (roomOpen) return TrimWhileRoomOpen(kind);
        if (liveMedia) return TrimWhileLiveHidden(kind);
        return TrimWhileIdleHidden();
    }

    /// EcoQoS for a hidden client. A live song keeps full speed for
    /// every process that carries it, exactly the set trimming keeps
    /// resident; a silent client slows everything but its audio, network
    /// and DRM services. An open room
    /// is never throttled.
    internal static bool ShouldThrottle(HostedProcessKind kind, bool roomOpen, bool liveMedia)
    {
        if (roomOpen) return false;
        if (liveMedia) return TrimWhileLiveHidden(kind);
        return kind is not (HostedProcessKind.Audio or HostedProcessKind.Network or HostedProcessKind.Cdm
            or HostedProcessKind.Unknown);
    }

    internal static string? QueryCommandLine(int pid)
    {
        if (pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0 || length > 64 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    return null;
                var info = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (info.Buffer == IntPtr.Zero || info.Length == 0) return null;
                return Marshal.PtrToStringUni(info.Buffer, info.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    private static bool ContainsType(string commandLine, string type) =>
        commandLine.Contains("--type=" + type, StringComparison.OrdinalIgnoreCase);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
