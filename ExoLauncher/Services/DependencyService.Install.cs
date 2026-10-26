using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Installing runtimes in one go: the plan (<see cref="Plan"/>), each installer downloaded with its measure and checked
/// (<see cref="Verify"/>), then ONE elevated run, so Windows asks once. That run is a PowerShell script Exo writes and
/// starts through a small bootstrap that checks the script's SHA-256 before running it. The script copies each installer
/// into a folder only administrators can write (under Windows\Temp, its permissions set as it is made), checks its SHA-256
/// again there, and only then runs it silently, in order, writing what each returned to a file Exo holds open; then Exo
/// reads this PC again and says per runtime what happened. While it runs, Exo holds every staged file open for reading
/// only, so nothing can swap one between Exo's check and the script's copy. Nothing is ever uninstalled, and Windows
/// Installer itself keeps a newer version in place.
/// </summary>
public sealed partial class DependencyService
{
    internal sealed record Progress(string Phase, string? Id, string? Name, int Index, int Count, long Bytes, long? Total, long DoneBytes, long AllBytes);

    /// <param name="Outcome">installed, newer (a newer one was already there), restart (installed; Windows needs a restart), failed.</param>
    internal sealed record ItemResult(string Id, string Name, string Outcome, string Message, int? Code);

    internal sealed record Result(bool Ok, string? Message, IReadOnlyList<ItemResult> Results, bool Declined = false, bool Canceled = false, object[]? Items = null);

    private sealed class Job(CancellationTokenSource cancel)
    {
        internal CancellationTokenSource Cancellation { get; } = cancel;
        internal bool Installing;
    }

    private Job? _job;

    // A size for what has none pinned until its download says (.NET's installers, WebView2's bootstrapper).
    private const long UnknownSize = 60L * 1024 * 1024;

    /// <summary>runtimes.cancel: stops the downloads; once Windows runs the installers it is too late, and says so.</summary>
    public object Cancel()
    {
        var job = _job;
        if (job is null) return new { ok = false, message = "Nothing is installing." };
        lock (job)
        {
            if (job.Installing) return new { ok = false, message = "Windows is installing them now. That can't be stopped halfway." };
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        return new { ok = true, message = "Stopping the downloads." };
    }

    /// <summary>
    /// What one install runs, in order. missing: what isn't on this PC (bulk leaves out what Microsoft no longer supports;
    /// named ids keep it, as a game asked for it). update: that and every older version. repair: every Visual C++ again.
    /// </summary>
    internal static IReadOnlyList<RuntimeStatus> Plan(IReadOnlyList<RuntimeStatus> statuses, string mode, IReadOnlyCollection<string>? ids)
    {
        var named = ids is { Count: > 0 } ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase) : null;
        var pool = statuses.Where(s => s.Runtime.Installable && (named is null || mode == "repair" || named.Contains(s.Runtime.Id)));
        bool Missing(RuntimeStatus s) => s.State == State.Missing && (named is not null || !s.EndOfSupport);
        var chosen = mode switch
        {
            "repair" => pool.Where(s => s.Runtime.IsVisualCpp),
            "update" => pool.Where(s => Missing(s) || s.State == State.Outdated),
            _ => pool.Where(Missing),
        };
        return chosen.OrderBy(s => s.Runtime.Order).ToList();
    }

    private sealed record Step(Runtime Runtime, bool Repair, string? Url, string? Sha256, string? Sha512, long Size, string Version)
    {
        internal string? File;
        internal string? RunSha256;
        internal string? Failure;
        internal long Bytes;
        internal long? Total;
    }

    /// <summary>runtimes.install: download, check and install the plan, then what each one did.</summary>
    internal async Task<Result> InstallAsync(IReadOnlyList<string>? ids, string mode, Action<Progress>? progress, CancellationToken ct)
    {
        mode = mode is "update" or "repair" ? mode : "missing";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var job = new Job(cancel);
        if (Interlocked.CompareExchange(ref _job, job, null) is not null)
            return new Result(false, "Runtimes are already installing.", []);
        var stage = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        var held = new List<FileStream>();
        try
        {
            await Task.Run(CleanupStaging, cancel.Token).ConfigureAwait(false);
            var dotnet = await DotnetReleasesAsync(cancel.Token).ConfigureAwait(false);
            var before = Evaluate(await Task.Run(() => Shows(CurrentSnapshot()), cancel.Token).ConfigureAwait(false), dotnet);
            var plan = Plan(before, mode, ids);
            if (plan.Count == 0)
                return new Result(true, ids is { Count: > 0 } ? "Already on this PC." : "Every runtime is up to date.", [], Items: before.Select(Shown).ToArray());
            AppLog.Info($"Runtimes: {mode} {string.Join(", ", plan.Select(s => s.Runtime.Id))}.");

            var steps = plan.Select(s => ToStep(s, mode == "repair", dotnet)).ToList();
            Directory.CreateDirectory(stage);
            await DownloadAllAsync(steps, stage, progress, cancel.Token).ConfigureAwait(false);

            var runnable = steps.Where(s => s.Failure is null).ToList();
            var codes = new Dictionary<string, int>(StringComparer.Ordinal);
            var declined = false;
            if (runnable.Count > 0)
            {
                lock (job) { cancel.Token.ThrowIfCancellationRequested(); job.Installing = true; }
                foreach (var step in runnable.Where(s => s.File is not null))
                    held.Add(new FileStream(step.File!, FileMode.Open, FileAccess.Read, FileShare.Read));
                var index = 0;
                (declined, codes) = await RunElevatedAsync(stage, runnable, held, id =>
                {
                    var step = runnable.First(s => s.Runtime.Id == id);
                    progress?.Invoke(new Progress("install", id, step.Runtime.Name, ++index, runnable.Count, 0, null, 0, 0));
                }).ConfigureAwait(false);
            }
            foreach (var stream in held) await stream.DisposeAsync().ConfigureAwait(false);
            held.Clear();

            if (declined)
                return new Result(false, "Windows' permission was declined, so nothing was installed.", [], Declined: true, Items: before.Select(Shown).ToArray());

            progress?.Invoke(new Progress("check", null, null, steps.Count, steps.Count, 0, null, 0, 0));
            ForgetSnapshot();
            var after = Evaluate(await Task.Run(() => Shows(CurrentSnapshot()), CancellationToken.None).ConfigureAwait(false), dotnet);
            var results = steps.Select(step => Outcome(step, before, after, codes)).ToList();
            foreach (var result in results) AppLog.Info($"Runtimes: {result.Id} {result.Outcome}{(result.Code is { } code ? $" ({code})" : "")}.");
            return new Result(results.All(r => r.Outcome != "failed"), null, results, Items: after.Select(Shown).ToArray());
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "Canceled. Nothing was installed.", [], Canceled: true);
        }
        catch (Exception ex)
        {
            // A file or Windows error names the staged installer's path: the log has it, the page does not.
            AppLog.Warn("Runtimes install: " + ex.GetType().Name + ": " + ex.Message);
            return new Result(false, "Exo couldn't finish installing them. The details are in the log.", []);
        }
        finally
        {
            foreach (var stream in held) await stream.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _job, null);
            try { RemoveStage(stage); }
            catch { /* A failed staging cleanup cannot hide the installer result. */ }
        }
    }

    // Direct3D 12's line for the page's rows, when the page has asked it (never asked here: an install from a game's notice).
    private static Snapshot Shows(Snapshot snapshot) => Graphics.IsValueCreated ? WithGraphics(snapshot) : snapshot;

    private static Step ToStep(RuntimeStatus status, bool repair, IReadOnlyDictionary<string, DotnetRelease> dotnet)
    {
        var r = status.Runtime;
        if (r.Id.StartsWith("dotnet", StringComparison.Ordinal))
        {
            var key = DotnetKey(r.Id);
            var arch = key[(key.IndexOf('-') + 1)..];
            if (dotnet.GetValueOrDefault(key) is { } release && release.Files.TryGetValue(arch, out var file))
                return new Step(r, repair, file.Url, null, file.Sha512, 0, release.Version);
            return new Step(r, repair, null, null, null, 0, status.Latest) { Failure = "Microsoft's .NET release list couldn't be read, so Exo can't check its installer. Try again." };
        }
        return new Step(r, repair, r.Url, r.Sha256, null, r.Size, status.Latest);
    }

    private static async Task DownloadAllAsync(List<Step> steps, string stage, Action<Progress>? progress, CancellationToken ct)
    {
        var downloads = steps.Where(s => s.Url is not null && s.Failure is null).ToList();
        long All() => downloads.Sum(s => s.Total ?? (s.Size > 0 ? s.Size : UnknownSize));
        long Done() => downloads.Sum(s => s.Bytes);
        for (var i = 0; i < downloads.Count; i++)
        {
            var step = downloads[i];
            var r = step.Runtime;
            var path = Path.Combine(stage, r.Id + Extension(step.Url!));
            void Report(long bytes, long? total)
            {
                step.Bytes = bytes;
                step.Total = total ?? step.Total;
                progress?.Invoke(new Progress("download", r.Id, r.Name, i + 1, downloads.Count, bytes, step.Total ?? (step.Size > 0 ? step.Size : null), Done(), All()));
            }
            try
            {
                Report(0, step.Size > 0 ? step.Size : null);
                await DownloadToAsync(step.Url!, path, Report, ct, Path.Combine(StagingRoot, "downloads"), step.Size > 0 ? step.Size : null).ConfigureAwait(false);
                progress?.Invoke(new Progress("verify", r.Id, r.Name, i + 1, downloads.Count, step.Bytes, step.Total, Done(), All()));
                await Task.Run(() =>
                {
                    Verify(path, r, step.Sha256, step.Sha512);
                    step.File = r.Kind == Kind.Zip ? Unzip(path, r, stage) : path;
                    if (r.Kind == Kind.Zip) RequireSigned(step.File, r);
                    step.RunSha256 = HashOf(step.File, SHA256.Create());
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
            {
                AppLog.Warn($"Runtimes: {r.Id} wasn't downloaded: {ex.GetType().Name}: {ex.Message}");
                step.Failure = ex is InvalidDataException data && data.Message.StartsWith(r.Name, StringComparison.Ordinal)
                    ? data.Message : "It couldn't be downloaded. Check the connection and try again.";
            }
        }
    }

    private static string Extension(string url)
    {
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return extension is ".exe" or ".msi" or ".zip" or ".nupkg" ? extension : ".exe";
    }

    /// <summary>The one installer a zip carries (<see cref="Runtime.Entry"/>), next to it in the stage; bounded in size.</summary>
    private static string Unzip(string zip, Runtime runtime, string stage)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(runtime.Entry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{runtime.Name}'s package doesn't hold its installer.");
        if (entry.Length is <= 0 or > ResumableDownload.MaxInstallerBytes) throw new InvalidDataException($"{runtime.Name}'s installer has an unexpected size.");
        var target = Path.Combine(stage, runtime.Id + Path.GetExtension(entry.Name).ToLowerInvariant());
        entry.ExtractToFile(target, overwrite: false);
        return target;
    }

    /// <summary>
    /// The script the elevated PowerShell runs: each step's installer copied into a folder only administrators can write,
    /// its SHA-256 checked again there, then run silently and waited for. What each returned goes to results.txt in the
    /// stage ("start id", "done id code"). Exits as the last step ends; its folder goes with it.
    /// </summary>
    internal static string Script(string stage, IEnumerable<(Runtime Runtime, bool Repair, string? File, string? Sha256)> steps)
    {
        static string Q(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$s = {Q(stage)}");
        script.AppendLine("$out = Join-Path $s 'results.txt'");
        script.AppendLine("function Say([string]$t) { $f = [IO.File]::Open($out, 'Append', 'Write', 'ReadWrite'); try { $b = [Text.Encoding]::UTF8.GetBytes($t + \"`n\"); $f.Write($b, 0, $b.Length) } finally { $f.Dispose() } }");
        // Administrators and SYSTEM only, set as the folder is made: nothing without the elevation can write in it.
        script.AppendLine("$acl = New-Object Security.AccessControl.DirectorySecurity");
        script.AppendLine("$acl.SetAccessRuleProtection($true, $false)");
        script.AppendLine("foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }");
        script.AppendLine("$w = Join-Path $env:SystemRoot ('Temp\\ExoRuntimes-' + [Guid]::NewGuid().ToString('N'))");
        script.AppendLine("[void][IO.Directory]::CreateDirectory($w, $acl)");
        script.AppendLine("$msi = Join-Path $env:SystemRoot 'System32\\msiexec.exe'");
        script.AppendLine("$dism = Join-Path $env:SystemRoot 'System32\\dism.exe'");
        script.AppendLine("function Take([string]$name, [string]$hash) { $to = Join-Path $w $name; [IO.File]::Copy((Join-Path $s $name), $to); $sha = [Security.Cryptography.SHA256]::Create(); $f = [IO.File]::OpenRead($to); try { $h = [BitConverter]::ToString($sha.ComputeHash($f)).Replace('-', '') } finally { $f.Dispose() }; if ($h -ne $hash) { throw 'changed' }; $to }");
        script.AppendLine("function Run([string]$file, [string]$arguments) { $i = New-Object Diagnostics.ProcessStartInfo($file, $arguments); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; $i.WorkingDirectory = Split-Path $file; $p = [Diagnostics.Process]::Start($i); $p.WaitForExit(); $p.ExitCode }");
        script.AppendLine("function Step([string]$id, [scriptblock]$body) { Say \"start $id\"; try { $code = @(& $body)[-1] } catch { $code = if (\"$_\" -eq 'changed') { -1001 } else { -1002 } }; Say \"done $id $code\" }");
        script.AppendLine("try {");
        foreach (var (runtime, repair, file, sha256) in steps)
        {
            var args = repair && runtime.RepairArgs is { } fix ? fix : runtime.Args;
            var take = file is null ? "" : $"(Take {Q(Path.GetFileName(file))} {Q(sha256 ?? "")})";
            var body = runtime.Kind switch
            {
                Kind.Feature => $"Run $dism {Q(args)}",
                Kind.DirectX => $"$x = {take}; $d = Join-Path $w 'directx'; [void][IO.Directory]::CreateDirectory($d); $c = Run $x ('/Q /C /T:\"' + $d + '\"'); if ($c -ne 0) {{ $c }} else {{ Run (Join-Path $d 'DXSETUP.exe') {Q(args)} }}",
                _ when runtime.Kind == Kind.Msi || file?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true => $"Run $msi ('/i \"' + {take} + '\" ' + {Q(args)})",
                _ => $"Run {take} {Q(args)}",
            };
            script.AppendLine($"  Step {Q(runtime.Id)} {{ {body} }}");
        }
        script.AppendLine("} finally { Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue; Say 'end' }");
        return script.ToString();
    }

    /// <summary>
    /// The one elevated run: Windows asks once. Declined: (true, nothing). Else what each step returned, by id, as the
    /// script wrote them; <paramref name="started"/> hears each step as it starts.
    /// </summary>
    private static async Task<(bool Declined, Dictionary<string, int> Codes)> RunElevatedAsync(string stage, List<Step> steps, List<FileStream> held, Action<string> started)
    {
        var script = Script(stage, steps.Select(s => (s.Runtime, s.Repair, s.File, s.RunSha256)));
        var scriptPath = Path.Combine(stage, "run.ps1");
        var bytes = new UTF8Encoding(false).GetBytes(script);
        await File.WriteAllBytesAsync(scriptPath, bytes).ConfigureAwait(false);
        held.Add(new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        var results = new FileStream(Path.Combine(stage, "results.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        held.Add(results);

        // The bootstrap runs the script from the bytes it hashed, so the file can't change between the check and the run.
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var bootstrap = $"$b = [IO.File]::ReadAllBytes('{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'); " +
            "$h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-', ''); " +
            $"if ($h -ne '{hash}') {{ exit 9 }}; & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString($b)))";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (true, []); }
        if (process is null) throw new InvalidOperationException("Windows didn't start the runtime installers.");

        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var reader = new StreamReader(results, Encoding.UTF8);
        void ReadNew()
        {
            text.Append(reader.ReadToEnd());
            var lines = text.ToString().Split('\n');
            text.Clear().Append(lines[^1]); // a line still being written
            foreach (var line in lines[..^1].Select(l => l.Trim()))
            {
                var parts = line.Split(' ');
                if (parts is ["start", var id] && steps.Any(s => s.Runtime.Id == id)) started(id);
                else if (parts is ["done", var done, var code] && int.TryParse(code, out var value)) codes[done] = value;
            }
        }
        using (process)
        {
            var exited = process.WaitForExitAsync();
            var deadline = Stopwatch.StartNew();
            // DISM fetching .NET 3.5 from Windows Update can take many minutes; a run that never ends is given up on after 90.
            while (!exited.IsCompleted && deadline.Elapsed < TimeSpan.FromMinutes(90))
            {
                await Task.WhenAny(exited, Task.Delay(400)).ConfigureAwait(false);
                ReadNew();
            }
            ReadNew();
            if (!exited.IsCompleted) AppLog.Warn("Runtimes: the installers were still running after 90 minutes.");
            else if (process.ExitCode == 9) AppLog.Warn("Runtimes: the install script changed before it ran, so it didn't.");
        }
        return (false, codes);
    }

    private static ItemResult Outcome(Step step, IReadOnlyList<RuntimeStatus> before, IReadOnlyList<RuntimeStatus> after, Dictionary<string, int> codes)
    {
        var r = step.Runtime;
        if (step.Failure is { } failure) return new ItemResult(r.Id, r.Name, "failed", failure, null);
        var was = before.FirstOrDefault(s => s.Runtime.Id == r.Id)?.State;
        var now = after.FirstOrDefault(s => s.Runtime.Id == r.Id)?.State;
        if (!codes.TryGetValue(r.Id, out var code))
            return new ItemResult(r.Id, r.Name, "failed", "It didn't run: the install stopped before it.", null);
        if (code is 3010 or 1641) return new ItemResult(r.Id, r.Name, "restart", "Installed. Restart Windows to finish.", code);
        if (code is 1638 or unchecked((int)0x80070666)) return new ItemResult(r.Id, r.Name, "newer", "A newer version is already installed.", code);
        if (code == 0 && now is State.Current)
            return new ItemResult(r.Id, r.Name, "installed", step.Repair && was is State.Current ? "Reinstalled." : "Installed.", code);
        if (code == 0) return new ItemResult(r.Id, r.Name, "failed", "Its installer finished, but Windows doesn't list it yet. Restart Windows, then check again.", code);
        // Some installers end with a code of their own after doing their job: what's on the PC now decides.
        if (now is State.Current && was is not State.Current) return new ItemResult(r.Id, r.Name, "installed", "Installed.", code);
        return new ItemResult(r.Id, r.Name, "failed", Words(code), code);
    }

    /// <summary>An installer's exit code in plain words (Windows Installer's, DISM's and the script's own).</summary>
    internal static string Words(int code) => code switch
    {
        1602 or 1223 or unchecked((int)0x800704C7) => "It was canceled.",
        1618 => "Another installation was running. Try again once it finishes.",
        1603 => "Its installer hit an error (1603).",
        1612 or 1620 => "Windows Installer couldn't find its files (1612).",
        1625 or 1643 => "A system policy blocked it.",
        1633 => "It doesn't run on this PC's processor.",
        5100 => "This PC doesn't meet its requirements.",
        unchecked((int)0x800F081F) => "Windows Update couldn't supply its files (0x800F081F).",
        unchecked((int)0x800F0906) => "Windows couldn't download its files. Check the connection (0x800F0906).",
        unchecked((int)0x800F0907) => "A group policy keeps Windows from getting its files (0x800F0907).",
        -1001 => "Its file changed after Exo checked it, so it didn't run.",
        -1002 => "Its installer didn't start.",
        < 0 => $"Its installer stopped with code 0x{code:X8}.",
        _ => $"Its installer stopped with code {code}.",
    };
}
