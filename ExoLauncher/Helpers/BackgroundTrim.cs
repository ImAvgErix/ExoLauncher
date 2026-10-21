using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Releases working-set pages for our own process tree while Exo is in the
/// background. Never touches processes outside the tree.
/// </summary>
internal static class BackgroundTrim
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessSetInformation = 0x0200;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ProcessMemoryPriority = 0;
    private const int ProcessPowerThrottling = 4;
    private const uint MemoryPriorityLow = 2;
    private const uint MemoryPriorityNormal = 5;
    private const uint PowerThrottlingExecutionSpeed = 0x1, PowerThrottlingIgnoreTimerResolution = 0x4;
    private static readonly Lock HostGate = new();
    private static bool _hostEfficient, _hostLive;

    /// The root pid plus every descendant in the current process table. A
    /// parent pid outlives its process: Windows reuses the number, so a
    /// process whose real parent exited can look like the child of whatever
    /// started later with that pid. A child older than its supposed parent is
    /// therefore not a child, and Exo itself is never part of another tree.
    internal static IReadOnlyList<int> TreePids(int rootPid)
    {
        var pids = new List<int> { rootPid };
        var self = Environment.ProcessId;
        var created = new Dictionary<int, long>();
        foreach (var pid in Descendants(rootPid, ProcessTable().Select(entry => (entry.Pid, entry.Parent)).ToArray(), (child, parent) =>
                     IsTreeChild(child, parent, self, created)))
            pids.Add(pid);
        return pids;
    }

    /// TreePids' membership test: never Exo itself, never a child older than its supposed parent.
    internal static bool IsTreeChild(int child, int parent, int self, Dictionary<int, long> created) =>
        child != self && !StartedBefore(child, parent, created);

    /// Breadth-first descendant selection from a (pid, parent) table.
    internal static List<int> Descendants(int root, IReadOnlyList<(int pid, int parent)> table,
        Func<int, int, bool> isChild)
    {
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var children = table.ToLookup(entry => entry.parent, entry => entry.pid);
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
        {
            foreach (var pid in children[current])
            {
                if (pid <= 0 || seen.Contains(pid) || !isChild(pid, current)) continue;
                seen.Add(pid);
                found.Add(pid);
                queue.Enqueue(pid);
            }
        }
        return found;
    }

    /// True only when both start times are readable and the child's is
    /// earlier; an unreadable process keeps the table's word for it.
    internal static bool StartedBefore(int child, int parent, Dictionary<int, long> cache)
    {
        var childStart = StartTicks(child, cache);
        var parentStart = StartTicks(parent, cache);
        return childStart != 0 && parentStart != 0 && childStart < parentStart;
    }

    private static long StartTicks(int pid, Dictionary<int, long> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        long ticks = 0;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try { if (GetProcessTimes(handle, out var created, out _, out _, out _)) ticks = created; }
            finally { CloseHandle(handle); }
        }
        cache[pid] = ticks;
        return ticks;
    }

    /// EcoQoS on the host process only — renderers, GPU and utilities keep
    /// full speed so music playback is never throttled. Held off while live
    /// sound runs (<see cref="Services.DiscordClient.Voice.LiveAudio"/>).
    internal static void SetHostEfficiencyMode(bool on)
    {
        lock (HostGate)
        {
            _hostEfficient = on;
            ApplyHost();
        }
    }

    /// Live sound started or stopped: while it runs the host is never in
    /// EcoQoS, its memory priority stays normal, and Windows honors its timer
    /// resolution even with the window hidden.
    internal static void SetHostLiveAudio(bool live)
    {
        lock (HostGate)
        {
            _hostLive = live;
            ApplyHost();
        }
    }

    // Under HostGate.
    private static void ApplyHost()
    {
        try
        {
            var process = GetCurrentProcess();
            var state = new ProcessPowerThrottlingState
            {
                Version = 1,
                ControlMask = PowerThrottlingExecutionSpeed | (_hostLive ? PowerThrottlingIgnoreTimerResolution : 0),
                StateMask = _hostEfficient && !_hostLive ? PowerThrottlingExecutionSpeed : 0,
            };
            SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
            if (!_hostLive) return;
            var memory = new MemoryPriorityInformation { MemoryPriority = MemoryPriorityNormal };
            SetProcessInformation(process, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriorityInformation>());
        }
        catch { }
    }

    /// <param name="efficient">
    /// EcoQoS for a process that only draws or stores for a hidden client:
    /// Windows schedules it for efficiency instead of speed. Null leaves the
    /// process's current state alone.
    /// </param>
    internal static bool ApplyManaged(ManagedProcessIdentity identity, bool low, bool trim,
        ProcessPriorityClass? priority = null, bool? efficient = null)
    {
        var access = ProcessQueryInformation | ProcessSetInformation | (trim ? ProcessSetQuota : 0);
        var handle = OpenProcess(access, false, identity.Pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) ||
                DateTime.FromFileTimeUtc(created).Ticks != identity.StartedUtcTicks) return false;
            var info = new MemoryPriorityInformation { MemoryPriority = low ? MemoryPriorityLow : MemoryPriorityNormal };
            var changed = SetProcessInformation(handle, ProcessMemoryPriority, ref info, Marshal.SizeOf<MemoryPriorityInformation>());
            if (priority is { } value) changed &= SetPriorityClass(handle, (uint)value);
            if (efficient is { } eco) changed &= SetEfficiency(handle, eco);
            if (trim) changed &= EmptyWorkingSet(handle);
            return changed;
        }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    /// Clears EcoQoS at once, off the trim loop's schedule: a room that opens
    /// must not draw its first frames throttled. Identity-checked like ApplyManaged.
    internal static void ClearEfficiency(ManagedProcessIdentity identity)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, identity.Pid);
        if (handle == IntPtr.Zero) return;
        try
        {
            if (GetProcessTimes(handle, out var created, out _, out _, out _) &&
                DateTime.FromFileTimeUtc(created).Ticks == identity.StartedUtcTicks)
                SetEfficiency(handle, false);
        }
        finally { CloseHandle(handle); }
    }

    /// Off hands a client's process back to Windows' own policy.
    private static bool SetEfficiency(IntPtr process, bool on)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = 1,
            ControlMask = on ? PowerThrottlingExecutionSpeed : 0,
            StateMask = on ? PowerThrottlingExecutionSpeed : 0,
        };
        return SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    /// Every process with its parent pid and executable file name (".exe" included).
    internal static List<(int Pid, int Parent, string Name)> ProcessTable()
    {
        var table = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                table.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
        }
        finally { CloseHandle(snapshot); }
        return table;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref MemoryPriorityInformation info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref ProcessPowerThrottlingState state, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
}
