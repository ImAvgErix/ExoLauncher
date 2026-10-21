using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

internal readonly record struct ManagedProcessIdentity(int Pid, long StartedUtcTicks);
internal sealed record ManagedProcessGroup(string Key, IReadOnlyList<ManagedProcessIdentity> Processes, bool MembershipComplete = true);
internal sealed record ManagedProcessReading(ManagedProcessIdentity Identity, long WorkingSetBytes, long PrivateBytes);
internal sealed record ManagedProcessUsage(string Key, int ProcessCount, int ReadableCount, long WorkingSetBytes, long PrivateBytes, bool Complete);
internal sealed record ManagedResourceSnapshot(long SampledAt, int ProcessCount, int ReadableCount,
    long WorkingSetBytes, long PrivateBytes, IReadOnlyList<ManagedProcessUsage> Groups, bool Complete);

internal static class ManagedProcessResources
{
    internal static ManagedProcessIdentity? Identify(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new ManagedProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch { return null; }
    }

    /// True while every member is still the process that was identified. A
    /// cached group is rebuilt as soon as one of them exits; the check is two
    /// syscalls per member, not a process-table walk.
    internal static bool AllAlive(ManagedProcessGroup group)
    {
        foreach (var identity in group.Processes)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, identity.Pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (WaitForSingleObject(handle, 0) != WaitTimeout || !StartedAt(handle, identity)) return false;
            }
            finally { CloseHandle(handle); }
        }
        return true;
    }

    internal static long? WorkingSetBytes(ManagedProcessIdentity identity)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, identity.Pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!StartedAt(handle, identity)) return null;
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            return K32GetProcessMemoryInfo(handle, ref counters, counters.Size) ? (long)counters.WorkingSetSize : null;
        }
        finally { CloseHandle(handle); }
    }

    private static bool StartedAt(IntPtr handle, ManagedProcessIdentity identity) =>
        GetProcessTimes(handle, out var created, out _, out _, out _) &&
        DateTime.FromFileTimeUtc(created).Ticks == identity.StartedUtcTicks;

    internal static ManagedProcessGroup HostedGroup(string key, ManagedProcessIdentity? root, string? directory)
    {
        if (root is not { } identity) return new ManagedProcessGroup(key, []);
        var observed = Identify(identity.Pid);
        if (observed is null || identity.StartedUtcTicks == 0 || string.IsNullOrEmpty(directory))
            return new ManagedProcessGroup(key, [identity], false);
        if (observed != identity) return new ManagedProcessGroup(key, []);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var complete = true;
        var identities = new List<ManagedProcessIdentity>();
        var descendants = BackgroundTrim.TreePids(identity.Pid).ToHashSet();
        foreach (var pid in descendants)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var started = process.StartTime.ToUniversalTime().Ticks;
                if (started < identity.StartedUtcTicks || process.HasExited) continue;
                if (process.MainModule?.FileName is not { } executable) { complete = false; continue; }
                if (!Path.GetFullPath(executable).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                identities.Add(new ManagedProcessIdentity(pid, started));
            }
            catch { complete = false; }
        }
        return Identify(identity.Pid) == identity
            ? new ManagedProcessGroup(key, identities, complete)
            : new ManagedProcessGroup(key, [], false);
    }

    internal static IReadOnlyList<ManagedProcessGroup> Normalize(IEnumerable<ManagedProcessGroup> groups)
    {
        var seen = new HashSet<int>();
        return groups.Select(group => new ManagedProcessGroup(group.Key,
            group.Processes.Where(identity => identity.Pid > 0 && seen.Add(identity.Pid)).ToArray(), group.MembershipComplete)).ToArray();
    }

    /// Working-set trim membership. Emptying a working set pages the next
    /// interaction back in, so a visible room and a live decoder stay resident.
    /// Hidden YouTube renderers, idle hosted clients, and GPU/crashpad helpers
    /// of a hidden-but-playing music client are released.
    /// Exo's own host and shell are released only while its window is
    /// minimized or hidden (a game in front, the tray), and the host never
    /// while live sound runs in it (a call: its noise model must stay in).
    internal static IReadOnlyList<ManagedProcessIdentity> TrimTargets(IEnumerable<ManagedProcessGroup> groups,
        IReadOnlySet<int> hiddenRendererPids, HostedTrimPolicy policy,
        Func<ManagedProcessIdentity, HostedProcessKind>? roleOf = null)
    {
        HostedProcessKind Role(ManagedProcessIdentity identity) => roleOf?.Invoke(identity) ?? HostedProcessKind.Unknown;
        return Normalize(groups).SelectMany(group => group.Processes.Where(identity => group.Key switch
        {
            "exo" => policy.ShellHidden && !policy.LiveAudio,
            "webview" => policy.ShellHidden || hiddenRendererPids.Contains(identity.Pid),
            "spotify" => HostedProcessClass.ShouldTrim(Role(identity), policy.MusicRoomOpen, policy.MusicPlaying),
            _ => false,
        })).ToArray();
    }

    /// EcoQoS membership: the hosted clients' processes that only draw or
    /// store for a closed room. Exo's own processes are never in it here; the
    /// host's efficiency mode follows the foreground separately.
    internal static IReadOnlyList<ManagedProcessIdentity> ThrottleTargets(IEnumerable<ManagedProcessGroup> groups,
        HostedTrimPolicy policy, Func<ManagedProcessIdentity, HostedProcessKind> roleOf) =>
        Normalize(groups).SelectMany(group => group.Processes.Where(identity => group.Key switch
        {
            "spotify" => HostedProcessClass.ShouldThrottle(roleOf(identity), policy.MusicRoomOpen, policy.MusicPlaying),
            _ => false,
        })).ToArray();

    internal static ManagedResourceSnapshot Capture(IEnumerable<ManagedProcessGroup> groups)
    {
        var normalized = Normalize(groups);
        var readings = new List<ManagedProcessReading>();
        foreach (var identity in normalized.SelectMany(group => group.Processes))
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks) continue;
                process.Refresh();
                var workingSet = process.WorkingSet64;
                var privateBytes = process.PrivateMemorySize64;
                if (!process.HasExited)
                    readings.Add(new ManagedProcessReading(identity, workingSet, privateBytes));
            }
            catch { }
        }
        return Summarize(normalized, readings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal static ManagedResourceSnapshot Summarize(IEnumerable<ManagedProcessGroup> groups,
        IEnumerable<ManagedProcessReading> readings, long sampledAt)
    {
        var byIdentity = readings.Where(reading => reading.WorkingSetBytes >= 0 && reading.PrivateBytes >= 0)
            .GroupBy(reading => reading.Identity).ToDictionary(group => group.Key, group => group.First());
        var usages = Normalize(groups).Select(group =>
        {
            var accepted = group.Processes.Where(byIdentity.ContainsKey).Select(identity => byIdentity[identity]).ToArray();
            return new ManagedProcessUsage(group.Key, group.Processes.Count, accepted.Length,
                accepted.Sum(reading => reading.WorkingSetBytes), accepted.Sum(reading => reading.PrivateBytes),
                group.MembershipComplete && accepted.Length == group.Processes.Count);
        }).ToArray();
        return new ManagedResourceSnapshot(sampledAt, usages.Sum(group => group.ProcessCount),
            usages.Sum(group => group.ReadableCount), usages.Sum(group => group.WorkingSetBytes),
            usages.Sum(group => group.PrivateBytes), usages, usages.All(group => group.Complete));
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public nuint PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}

internal sealed class ManagedProcessPolicyState
{
    private const long RetrimGrowthBytes = 32L << 20;
    private readonly Dictionary<ManagedProcessIdentity, (bool Low, ProcessPriorityClass? Priority, bool Efficient, bool Eligible, long TrimmedBytes)> _applied = new();
    /// <param name="refresh">
    /// Re-assert an applied EcoQoS: opening a room clears it directly,
    /// outside this bookkeeping, and a room closed again before the next
    /// tick would otherwise stay at full speed.
    /// </param>
    internal (bool Apply, bool Trim) Next(ManagedProcessIdentity identity, bool low, ProcessPriorityClass? priority,
        bool efficient, bool eligible, bool retrim = false, bool refresh = false)
    {
        if (!_applied.TryGetValue(identity, out var prior))
            return (true, eligible);
        var changed = (prior.Low, prior.Priority, prior.Efficient, prior.Eligible) != (low, priority, efficient, eligible);
        var trim = eligible && (!prior.Eligible || retrim);
        return (changed || trim || (efficient && refresh), trim);
    }
    /// A trimmed process is trimmed again only once it has paged more than
    /// 32 MB back in; emptying a set that barely grew only buys page faults.
    internal bool GrewSinceTrim(ManagedProcessIdentity identity) =>
        _applied.TryGetValue(identity, out var prior) &&
        ManagedProcessResources.WorkingSetBytes(identity) is { } current && current - prior.TrimmedBytes > RetrimGrowthBytes;
    internal void Accept(ManagedProcessIdentity identity, bool low, ProcessPriorityClass? priority, bool efficient,
        bool eligible, bool trimmed)
    {
        var trimmedBytes = trimmed ? ManagedProcessResources.WorkingSetBytes(identity) ?? 0
            : _applied.TryGetValue(identity, out var prior) ? prior.TrimmedBytes : 0;
        _applied[identity] = (low, priority, efficient, eligible, trimmedBytes);
    }
    internal void Retain(IReadOnlySet<ManagedProcessIdentity> live)
    {
        foreach (var identity in _applied.Keys.Where(identity => !live.Contains(identity)).ToArray()) _applied.Remove(identity);
    }
}
