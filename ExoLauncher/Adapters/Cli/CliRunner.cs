using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters.Cli;

/// <summary>Runs external CLI backends and streams stdout/stderr into progress callbacks.</summary>
public static class CliRunner
{
    internal const int MaxRetainedOutputChars = 256 * 1024;
    internal const int MaxCompleteJsonChars = 16 * 1024 * 1024;

    /// <summary>The CLI runs Exo has going right now (scans, owned lists, downloads), by process id.</summary>
    private static readonly ConcurrentDictionary<int, byte> Running = new();

    private sealed class RunningLease(int pid) : IDisposable
    {
        public void Dispose() => Running.TryRemove(pid, out _);
    }

    private static RunningLease Track(Process proc)
    {
        Running[proc.Id] = 0;
        return new RunningLease(proc.Id);
    }

    /// <summary>
    /// True when a process of this name runs that none of Exo's own CLI runs started: one an earlier
    /// session left behind, or the user's. A one-file tool (Legendary) runs as a parent and a child of
    /// the same name, so the child of one of Exo's runs is Exo's too.
    /// </summary>
    internal static bool IsRunningOutsideExo(string processName)
    {
        var exe = processName + ".exe";
        var table = BackgroundTrim.ProcessTable();
        var parents = new Dictionary<int, int>();
        foreach (var (pid, parent, _) in table) parents[pid] = parent;
        return table.Any(entry => string.Equals(entry.Name, exe, StringComparison.OrdinalIgnoreCase) &&
            !Running.ContainsKey(entry.Pid) &&
            !(parents.TryGetValue(entry.Pid, out var parent) && Running.ContainsKey(parent)));
    }

    /// <summary>Keep complete machine-readable stdout; reject oversize output instead of returning a corrupt tail.</summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunJsonAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var trustedExecutable = Services.VerifiedGitHubReleaseDownloader.AcquireExecutionLease(fileName);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException($"Failed to start {fileName}");
        using var running = Track(proc);

        void StopProcess()
        {
            try { if (!proc.HasExited) ProcessTermination.Kill(proc, entireProcessTree: true); }
            catch { /* Preserve the read/cancellation failure. */ }
        }

        async Task<string> ReadAsync(StreamReader reader, bool complete)
        {
            try { return await ReadOutputAsync(reader, complete, ct).ConfigureAwait(false); }
            catch { StopProcess(); throw; }
        }

        var stdout = ReadAsync(proc.StandardOutput, complete: true);
        var stderr = ReadAsync(proc.StandardError, complete: false);
        try
        {
            await Task.WhenAll(stdout, stderr, proc.WaitForExitAsync(ct)).ConfigureAwait(false);
            return (proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            StopProcess();
            try
            {
                using var exitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(exitBudget.Token).ConfigureAwait(false);
            }
            catch { /* Do not hide the original output error. */ }
            throw;
        }
    }

    internal static async Task<string> ReadOutputAsync(
        TextReader reader, bool complete, CancellationToken ct, int completeLimit = MaxCompleteJsonChars)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (complete && count > completeLimit - result.Length)
                throw new InvalidDataException($"CLI JSON response exceeds the {completeLimit}-character limit.");
            if (!complete)
            {
                var excess = result.Length + count - MaxRetainedOutputChars;
                if (excess > 0) result.Remove(0, excess);
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    internal static void AppendTail(StringBuilder target, string line)
    {
        if (line.Length >= MaxRetainedOutputChars)
        {
            target.Clear();
            target.Append(line.AsSpan(line.Length - MaxRetainedOutputChars + Environment.NewLine.Length));
        }
        else
        {
            var excess = target.Length + line.Length + Environment.NewLine.Length - MaxRetainedOutputChars;
            if (excess > 0) target.Remove(0, excess);
            target.Append(line);
        }
        target.AppendLine();
    }
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var trustedExecutable = Services.VerifiedGitHubReleaseDownloader.AcquireExecutionLease(fileName);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputGate = new object();
        var callbackFailed = false;
        void Receive(StringBuilder target, string? line)
        {
            if (line is null) return;
            lock (outputGate)
            {
                AppendTail(target, line);
                if (callbackFailed) return;
                try { onLine?.Invoke(line); }
                catch (Exception ex)
                {
                    callbackFailed = true;
                    AppLog.Warn("CLI progress callback failed: " + ex.Message);
                }
            }
        }

        proc.OutputDataReceived += (_, e) =>
        {
            Receive(stdout, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            Receive(stderr, e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start {fileName}");
        using var running = Track(proc);

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                    ProcessTermination.Kill(proc, entireProcessTree: true);
            }
            catch { /* best-effort cancel */ }
            try
            {
                using var exitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(exitBudget.Token).ConfigureAwait(false);
            }
            catch { /* Preserve the original cancellation if the OS cannot reap the process promptly. */ }
            throw;
        }

        lock (outputGate) return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static string? ResolveOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

}
