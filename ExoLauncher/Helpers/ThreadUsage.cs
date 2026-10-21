using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// CPU by thread, for app.log: each of this process's threads by its name (a .NET thread's, or the description a
/// library gave it) or else the module it started in (deep_filter.dll, coreclr.dll, ...), summed per name, as a share
/// of one core since the last reading. It reads every thread and module, so once a minute and never on an audio thread.
/// </summary>
internal sealed partial class ThreadUsage
{
    private const uint QueryLimitedInformation = 0x0800;
    private Dictionary<int, long> _last = [];
    private long _lastAt;

    /// <summary>The busiest names since the last reading ("deep_filter.dll (3 threads) 31.2%, ..."); empty the first time.</summary>
    internal string Busiest(int count = 6)
    {
        using var self = Process.GetCurrentProcess();
        var now = Stopwatch.GetTimestamp();
        var wall = (now - _lastAt) * (10_000_000.0 / Stopwatch.Frequency);
        (nint From, nint To, string Name)[] modules;
        try { modules = [.. self.Modules.Cast<ProcessModule>().Select(m => (m.BaseAddress, m.BaseAddress + m.ModuleMemorySize, m.ModuleName))]; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { modules = []; }
        var seen = new Dictionary<int, long>();
        var named = new Dictionary<string, (long Cpu, int Threads)>();
        foreach (ProcessThread thread in self.Threads)
        {
            using (thread)
            {
                try
                {
                    var cpu = thread.TotalProcessorTime.Ticks;
                    seen[thread.Id] = cpu;
                    var name = Description(thread.Id) ?? Module(modules, thread.StartAddress);
                    var (sum, threads) = named.GetValueOrDefault(name);
                    named[name] = (sum + cpu - _last.GetValueOrDefault(thread.Id), threads + 1);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // it ended
            }
        }
        var first = _lastAt == 0;
        (_last, _lastAt) = (seen, now);
        if (first || wall <= 0) return "";
        return string.Join(", ", named.OrderByDescending(p => p.Value.Cpu).Take(count).Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Key}{(p.Value.Threads > 1 ? $" ({p.Value.Threads} threads)" : "")} {100.0 * p.Value.Cpu / wall:0.0}%")));
    }

    private static string Module((nint From, nint To, string Name)[] modules, nint start)
    {
        foreach (var (from, to, name) in modules) if (start >= from && start < to) return name;
        return "unknown";
    }

    // The thread's description (a .NET thread's name is set as one), or null when it has none.
    private static string? Description(int id)
    {
        var handle = OpenThread(QueryLimitedInformation, false, (uint)id);
        if (handle == 0) return null;
        try
        {
            if (GetThreadDescription(handle, out var text) < 0 || text == 0) return null;
            try { return Marshal.PtrToStringUni(text) is { Length: > 0 } name ? name : null; }
            finally { LocalFree(text); }
        }
        finally { CloseHandle(handle); }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
