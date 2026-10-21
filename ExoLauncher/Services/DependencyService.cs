using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace ExoLauncher.Services;

/// <summary>
/// Detect common runtimes. Offer official installers with consent — never silent-force.
/// </summary>
public sealed class DependencyService
{
    private sealed class InstallJob(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool Installing;
    }
    internal sealed record InstallResult(bool ok, string message, int? exitCode = null, bool restartRequired = false, string? status = null);
    private readonly ConcurrentDictionary<string, InstallJob> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<DependencyInfo>> _detect;
    private readonly Func<string, string, Action<long, long?>?, CancellationToken, Task> _download;
    private readonly Action<string> _verify;
    private readonly Func<string, string?, Task<int>> _runInstaller;
    private readonly Action _cleanupStaging;

    public DependencyService()
    {
        _detect = DetectAll;
        _download = (url, destination, progress, ct) => DownloadToAsync(url, destination, progress, ct,
            cacheDirectory: Path.Combine(Path.GetTempPath(), "ExoLauncherDependencies", "downloads"));
        _verify = RequireMicrosoftInstaller;
        _runInstaller = RunInstallerAsync;
        _cleanupStaging = CleanupStaging;
    }

    public object CancelInstall(string dependencyId)
    {
        if (!_downloads.TryGetValue(dependencyId, out var job))
            return new { ok = false, message = "No download is running." };
        lock (job)
        {
            if (job.Installing) return new { ok = false, message = "The installer is running. Use its Cancel button to stop installation." };
            try { job.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            return new { ok = true, message = "Canceling download…" };
        }
    }
    public IReadOnlyList<DependencyInfo> DetectAll()
    {
        return new[]
        {
            DetectVcRedist(),
            DetectDirectX(),
            DetectWebView2(),
        };
    }

    public IReadOnlyList<DependencyInfo> GetMissingRequired(GameEntry game)
    {
        // Launcher tooling is not a game requirement. Only prompt for explicitly declared dependencies.
        return DetectAll().Where(d => d.Status == "Missing" &&
            game.Deps.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Downloads and verifies the official installer, then observes its result without blocking the UI.
    /// </summary>
    public async Task<object> OfferInstallAsync(string dependencyId, Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dep = (await Task.Run(_detect, cancellationToken).ConfigureAwait(false)).FirstOrDefault(d =>
            string.Equals(d.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
        if (dep is null)
            return new { ok = false, message = "Unknown dependency." };
        if (string.Equals(dep.Status, "Present", StringComparison.OrdinalIgnoreCase))
            return new { ok = true, message = $"{dep.Name} is already on this PC." };

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(4));
        var job = new InstallJob(cancellation);
        if (!_downloads.TryAdd(dependencyId, job))
            return new { ok = false, message = "This dependency is already downloading or installing." };
        var stage = Path.Combine(Path.GetTempPath(), "ExoLauncherDependencies", Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(_cleanupStaging, cancellation.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(dep.OfficialUrl) ||
                !LooksLikeInstaller(dep.OfficialUrl))
            {
                if (string.IsNullOrWhiteSpace(dep.OfficialUrl))
                    return new { ok = false, message = "Exo cannot install this one. Get it from its official site." };
                StartVisible(dep.OfficialUrl);
                return new { ok = true, status = "handoff", message = $"Opened the {dep.Name} download page. Finish installation there, then Check again." };
            }

            var dest = Path.Combine(stage, "installer.exe");
            await _download(dep.OfficialUrl, dest, progress, cancellation.Token).ConfigureAwait(false);
            _verify(dest);
            BeginInstaller(job);

            // Microsoft's unattended switches: the WebView2 bootstrapper's
            // /silent, the VC++ bundle's /passive (a progress bar, nothing to
            // click). Windows still asks for permission; Exo shows the progress.
            var args = string.Equals(dep.Id, "webview2", StringComparison.OrdinalIgnoreCase)
                ? "/silent /install"
                : "/install /passive /norestart";
            return await ObserveInstallerAsync(dep, dest, args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, message = cancellationToken.IsCancellationRequested || cancellation.IsCancellationRequested
                ? "The download was canceled or timed out. Try again." : "The download stopped. Try again." };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
        finally
        {
            _downloads.TryRemove(dependencyId, out _);
            try { RemoveStage(stage); }
            catch { /* A failed staging cleanup cannot hide the installer result. */ }
        }
    }

    private static void RemoveStage(string stage)
    {
        if (!Directory.Exists(stage) || (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var name in new[] { "installer.exe", "installer.exe.partial" })
            File.Delete(Path.Combine(stage, name));
        Directory.Delete(stage); // Do not recurse into unexpected or user-authored contents.
    }

    private static void CleanupStaging()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "ExoLauncherDependencies");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var stage in Directory.EnumerateDirectories(root).Take(128))
            {
                if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _) ||
                    Directory.GetLastWriteTimeUtc(stage) > DateTime.UtcNow.AddDays(-1)) continue;
                try { RemoveStage(stage); } catch { /* A running installer can still own the file. */ }
            }
        }
        catch { /* Best-effort expiry of Exo-owned installer payloads only. */ }
    }

    private static bool LooksLikeInstaller(string url) =>
        url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("fwlink", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("aka.ms", StringComparison.OrdinalIgnoreCase);

    internal static async Task DownloadToAsync(string url, string dest, Action<long, long?>? progress,
        CancellationToken cancellationToken, string? cacheDirectory = null)
    {
        using var http = ExoHttp.Create(Timeout.InfiniteTimeSpan, new SocketsHttpHandler { AllowAutoRedirect = false });
        await ResumableDownload.DownloadAsync(new Uri(url), dest, cacheDirectory ?? Path.GetDirectoryName(Path.GetFullPath(dest))!,
            (request, ct) => SendFollowingInstallerRedirectsAsync(http, request, ct),
            null, null, progress, cancellationToken).ConfigureAwait(false);
    }

    /// Follows at most five redirects, each to an https host on the PRIVACY.md installers row.
    private static async Task<HttpResponseMessage> SendFollowingInstallerRedirectsAsync(
        HttpClient http, HttpRequestMessage initialRequest, CancellationToken ct)
    {
        var current = initialRequest.RequestUri ?? throw new InvalidOperationException("Missing download URL.");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsInstallerHost(current))
                throw new InvalidOperationException("The installer download redirected to an unlisted host: " + current.Host);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var header in initialRequest.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidOperationException("The installer download redirect had no destination.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new InvalidOperationException("The installer download redirected too many times.");
    }

    internal static bool IsInstallerHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        NetworkDestinations.Hosts.Any(d => d.PrivacyRow == NetworkDestinations.Installers && NetworkDestinations.Covers(d.Host, uri.IdnHost));

    private static void BeginInstaller(InstallJob job)
    {
        lock (job)
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            job.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            job.Installing = true;
        }
    }

    private async Task<InstallResult> ObserveInstallerAsync(DependencyInfo dependency, string path, string? arguments)
    {
        var exitCode = await _runInstaller(path, arguments).ConfigureAwait(false);
        var after = (await Task.Run(_detect).ConfigureAwait(false)).FirstOrDefault(item => item.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase));
        var present = after?.Status.Equals("Present", StringComparison.OrdinalIgnoreCase) == true;
        if (exitCode is 3010 or 1641)
            return new InstallResult(true, $"{dependency.Name} installation completed. Restart Windows to finish setup.", exitCode, true, after?.Status);
        if (exitCode == 1602)
            return new InstallResult(false, $"{dependency.Name} installation was canceled.", exitCode, false, after?.Status);
        if (exitCode != 0)
            return new InstallResult(false, $"{dependency.Name} installer exited with code {exitCode}. You can retry.", exitCode, false, after?.Status);
        return present
            ? new InstallResult(true, $"{dependency.Name} is installed and detected.", exitCode, false, after!.Status)
            : new InstallResult(false, $"{dependency.Name} installer finished, but the runtime is still missing. Retry or use its official download page.", exitCode, false, after?.Status);
    }

    private static async Task<int> RunInstallerAsync(string fileName, string? arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Windows did not start the runtime installer.");
        // Once Windows owns the installer, cancellation belongs to its own UI.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void RequireMicrosoftInstaller(string path)
    {
        if (!AuthenticodeVerifier.HasPublisher(path, "Microsoft Corporation"))
            throw new IOException("The installer could not be verified as signed by Microsoft. Retry or use the official download page.");
    }

    private static void StartVisible(string fileName, string? arguments = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
        };
        if (!string.IsNullOrWhiteSpace(arguments))
            psi.Arguments = arguments;
        System.Diagnostics.Process.Start(psi);
    }

    private static DependencyInfo DetectVcRedist()
    {
        // Presence of recent VC++ runtime keys is a practical signal, not a full matrix.
        var present = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            present = key?.GetValue("Installed") is int i && i == 1;
        }
        catch { }

        return new DependencyInfo
        {
            Id = "vcredist",
            Name = "Visual C++ Redistributable",
            Status = present ? "Present" : "Missing",
            Detail = present
                ? "VC++ 2015–2022 x64 runtime key found."
                : "VC++ 2015–2022 x64 runtime not found.",
            CanOfferInstall = true,
            OfficialUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
        };
    }

    private static DependencyInfo DetectDirectX()
    {
        var sys = Environment.SystemDirectory;
        var d3d = File.Exists(Path.Combine(sys, "d3d11.dll"));
        return new DependencyInfo
        {
            Id = "directx",
            Name = "DirectX",
            Status = d3d ? "Present" : "Missing",
            Detail = d3d
                ? "d3d11.dll present (OS component)."
                : "DirectX runtime files are missing.",
            CanOfferInstall = true,
            OfficialUrl = "https://www.microsoft.com/download/details.aspx?id=35",
        };
    }

    private static DependencyInfo DetectWebView2()
    {
        // A stale registration is common after a failed Edge update: the
        // Clients key still names a version whose Application folder has been
        // emptied, and every CreateCoreWebView2Environment call fails with
        // "Unable to find the specified file". Verify the browser binary, not
        // just the registry value.
        var pv = WebViewRuntimeLocator.RegisteredVersion();
        var binariesPresent =
            WebViewRuntimeLocator.IsRuntimeExe(WebViewRuntimeLocator.RegisteredEvergreenExe())
            || WebViewRuntimeLocator.IsRuntimeFolder(WebViewRuntimeLocator.DiscoverInstalledRuntimeFolder());

        return new DependencyInfo
        {
            Id = "webview2",
            Name = "WebView2 Runtime",
            Status = binariesPresent ? "Present" : "Missing",
            Detail = binariesPresent
                ? $"Evergreen WebView2 runtime {pv} verified."
                : pv is null
                    ? "Required for Music catalog and store sign-in."
                    : $"WebView2 {pv} is registered but its files are missing — run the installer below to repair.",
            CanOfferInstall = true,
            OfficialUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
        };
    }

}
