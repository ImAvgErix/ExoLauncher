using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Suspends and resumes a hosted client's whole process tree. A suspended
/// process spends no CPU, and once its working set is emptied nothing can
/// fault the pages back in, so the release actually holds — which a trim on
/// a running client never did. Every process is opened with the narrowest
/// rights the operation needs, checked against its recorded start time (a
/// reused pid is someone else), and released again straight away.
/// </summary>
internal static class ProcessFreezer
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessSuspendResume = 0x0800;

    /// Suspends every process, then empties its working set. Returns the
    /// identities that were actually suspended so the caller can resume
    /// exactly those. Exo itself and its own WebView2 environment (the shell
    /// and YouTube Music) are never suspended: a frozen shell hangs Exo.
    internal static List<ManagedProcessIdentity> Freeze(IEnumerable<ManagedProcessIdentity> processes)
    {
        var frozen = new List<ManagedProcessIdentity>();
        foreach (var identity in processes)
        {
            if (identity.Pid == Environment.ProcessId || OwnWebView(identity.Pid)) continue;
            var handle = OpenProcess(ProcessSuspendResume | ProcessQueryInformation | ProcessSetQuota, false, identity.Pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (!Matches(handle, identity) || NtSuspendProcess(handle) != 0) continue;
                frozen.Add(identity);
                try { EmptyWorkingSet(handle); } catch { }
            }
            catch { }
            finally { CloseHandle(handle); }
        }
        return frozen;
    }

    internal static void Thaw(IEnumerable<ManagedProcessIdentity> processes)
    {
        foreach (var identity in processes)
        {
            var handle = OpenProcess(ProcessSuspendResume | ProcessQueryLimitedInformation, false, identity.Pid);
            if (handle == IntPtr.Zero) continue;
            try { if (Matches(handle, identity)) NtResumeProcess(handle); }
            catch { }
            finally { CloseHandle(handle); }
        }
    }

    private static bool Matches(IntPtr handle, ManagedProcessIdentity identity) =>
        GetProcessTimes(handle, out var created, out _, out _, out _) &&
        DateTime.FromFileTimeUtc(created).Ticks == identity.StartedUtcTicks;

    /// Exo's WebView2 processes carry its user-data folder on their command line.
    private static bool OwnWebView(int pid) =>
        HostedProcessClass.QueryCommandLine(pid) is { } commandLine &&
        commandLine.Contains(WebViewHostProfile.UserDataFolder, StringComparison.OrdinalIgnoreCase);

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
