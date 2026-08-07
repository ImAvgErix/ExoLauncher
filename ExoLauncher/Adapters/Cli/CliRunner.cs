using System.Diagnostics;
using System.Text;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>Runs external CLI backends and streams stdout/stderr into progress callbacks.</summary>
public static class CliRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken ct)
    {
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

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start {fileName}");

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
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* best-effort cancel */ }
            throw;
        }

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
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

    public static string? FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);
}
