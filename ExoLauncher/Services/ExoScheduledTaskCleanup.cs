using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// One-time migration of the exact legacy Exo task names, after first paint.
/// </summary>
internal static class ExoScheduledTaskCleanup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly string[] LegacyTaskNames =
    [
        "Exo-SteamWebHelperTrim", "Exo-SteamMemoryGuard", "Exo App Trim", "Exo-AppTrim",
        "Exo-NvidiaTrayHide", "Exo-NvidiaDisplayPersist", "Exo-Nvidia", "Exo-Discord",
    ];

    public static Task RemoveLeftoversAsync(CancellationToken ct = default) => RunMigrationAsync(
        Path.Combine(PathHelper.AppDataDir, "legacy-task-cleanup-v1.json"), DeleteLegacyTaskAsync, ct);

    internal static async Task RunMigrationAsync(
        string statePath, Func<string, CancellationToken, Task<bool>> deleteTask, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completed = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(statePath) && new FileInfo(statePath).Length <= 4096)
                    completed.UnionWith(JsonSerializer.Deserialize<string[]>(
                        await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false)) ?? []);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* An invalid migration marker only retries the allow-list. */ }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(35));
            foreach (var name in LegacyTaskNames)
            {
                if (completed.Contains(name)) continue;
                try
                {
                    if (!await deleteTask(name, budget.Token).ConfigureAwait(false)) continue;
                    completed.Add(name);
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temporary,
                            JsonSerializer.Serialize(LegacyTaskNames.Where(completed.Contains)), budget.Token).ConfigureAwait(false);
                        File.Move(temporary, statePath, overwrite: true);
                    }
                    finally { try { File.Delete(temporary); } catch { } }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Debug("Legacy task cleanup deferred: " + ex.GetType().Name); }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> DeleteLegacyTaskAsync(string name, CancellationToken ct)
    {
        // Task Scheduler's HRESULT identifies an absent task regardless of the
        // Windows display language. The helper process bounds a stuck COM call.
        var script = "$ErrorActionPreference='Stop';try{$s=New-Object -ComObject Schedule.Service;$s.Connect();" +
                     "$s.GetFolder('\\').DeleteTask('" + name.Replace("'", "''", StringComparison.Ordinal) + "',0);exit 0}" +
                     "catch{for($e=$_.Exception;$null -ne $e;$e=$e.InnerException){" +
                     "if($e.HResult -eq -2147024894 -or $e.HResult -eq -2147024893){exit 0}};exit 1}";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, budget.Token),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null, budget.Token),
                process.WaitForExitAsync(budget.Token)).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
