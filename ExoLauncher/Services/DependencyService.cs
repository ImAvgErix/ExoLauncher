using System.Security.Cryptography;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Game runtimes: what this PC has of the catalog (DependencyService.Catalog.cs), what a game says it needs
/// (DependencyService.Games.cs), and installing them, asked first and with one Windows permission prompt
/// (DependencyService.Install.cs). Installers come only from their makers' servers, each checked against its pinned
/// SHA-256 (or Microsoft's own SHA-512 for .NET) and its maker's signature. Exo never uninstalls a runtime.
/// </summary>
public sealed partial class DependencyService
{
    private static readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "ExoLauncherDependencies");

    /// <summary>runtimes.list: every runtime that applies to this PC, with its version, the newest, and its state.</summary>
    public async Task<object> ListAsync(CancellationToken ct = default)
    {
        var dotnet = DotnetReleasesAsync(ct);
        var snapshot = await Task.Run(() => WithGraphics(CurrentSnapshot()), ct).ConfigureAwait(false);
        var statuses = Evaluate(snapshot, await dotnet.ConfigureAwait(false));
        return new { ok = true, busy = _job is not null, items = statuses.Select(Shown).ToArray() };
    }

    /// <summary>The snapshot with what Direct3D 12 does on this PC's graphics, for the page (asked once a session).</summary>
    internal static Snapshot WithGraphics(Snapshot snapshot) =>
        snapshot with { Graphics = Graphics.Value ?? "Windows couldn't say what Direct3D 12 does on this PC's graphics." };

    /// <summary>One runtime as the page shows it.</summary>
    internal static object Shown(RuntimeStatus status) => new
    {
        id = status.Runtime.Id,
        name = status.Runtime.Name,
        group = status.Runtime.Group switch { Group.VisualCpp => "vc", Group.DirectX => "directx", Group.DotNet => "dotnet", _ => "other" },
        installed = status.Found?.Shown,
        latest = status.Latest,
        state = status.State switch { State.Current => "current", State.Outdated => "outdated", State.Missing => "missing", _ => "info" },
        detail = status.Detail,
        // The download's size where Exo pins it (0: known once it starts, as for .NET).
        size = status.Runtime.Size,
        endOfSupport = status.EndOfSupport,
        // "Install what's missing" leaves out what Microsoft no longer supports; a game that names one still gets it.
        bulk = status.Runtime.Installable && !status.EndOfSupport,
    };

    /// <summary>deps.list (Settings › About, First run): the runtimes as present or missing; one Microsoft no longer supports is only offered when a game asks.</summary>
    public IReadOnlyList<DependencyInfo> DetectAll() =>
        Evaluate(CurrentSnapshot(), _dotnet?.Releases).Where(s => s.Runtime.Installable && !(s.State == State.Missing && s.EndOfSupport)).Select(Legacy).ToList();

    private static DependencyInfo Legacy(RuntimeStatus status) => new()
    {
        Id = status.Runtime.Id,
        Name = status.Runtime.Name,
        Status = status.State == State.Missing ? "Missing" : "Present",
        Detail = status.Found is { } found ? found.Shown : "Not installed",
        CanOfferInstall = status.Runtime.Installable,
        OfficialUrl = status.Runtime.Url,
    };

    /// <summary>
    /// The runtimes a game's store says it needs (<see cref="RuntimesFor"/>) that this PC doesn't have. Only missing ones
    /// stop Play: an older version still runs what was built for it.
    /// </summary>
    public IReadOnlyList<DependencyInfo> GetMissingRequired(GameEntry game)
    {
        try
        {
            var wanted = RuntimesFor(game);
            if (wanted.Count == 0) return [];
            return Evaluate(CurrentSnapshot(), _dotnet?.Releases)
                .Where(s => s.State == State.Missing && wanted.Contains(s.Runtime.Id, StringComparer.OrdinalIgnoreCase))
                .Select(Legacy).ToList();
        }
        catch (Exception ex)
        {
            // A check that can't finish never stands between you and Play.
            AppLog.Warn("Runtimes: a game's needs couldn't be checked: " + ex.GetType().Name + ": " + ex.Message);
            return [];
        }
    }

    /// <summary>deps.offerInstall: one runtime, through the same install as the rest.</summary>
    public async Task<object> OfferInstallAsync(string dependencyId, Action<long, long?>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await InstallAsync([dependencyId], "missing", update =>
        {
            if (update.Phase == "download") progress?.Invoke(update.Bytes, update.Total);
        }, cancellationToken).ConfigureAwait(false);
        var mine = result.Results.FirstOrDefault();
        return new
        {
            ok = result.Ok,
            message = mine?.Message ?? result.Message,
            exitCode = mine?.Code,
            restartRequired = mine?.Outcome == "restart",
            status = mine?.Outcome,
        };
    }

    /// <summary>deps.cancelInstall.</summary>
    public object CancelInstall(string dependencyId) => Cancel();

    private static void RemoveStage(string stage)
    {
        if (!Directory.Exists(stage) || (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) return;
        // Exo's own files only, at the top: never recurse into unexpected or user-authored contents.
        foreach (var file in Directory.EnumerateFiles(stage).Take(256))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
        Directory.Delete(stage);
    }

    private static void CleanupStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot) || (File.GetAttributes(StagingRoot) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var stage in Directory.EnumerateDirectories(StagingRoot).Take(128))
            {
                if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _) ||
                    Directory.GetLastWriteTimeUtc(stage) > DateTime.UtcNow.AddDays(-1)) continue;
                try { RemoveStage(stage); } catch { /* A running installer can still own the file. */ }
            }
        }
        catch { /* Best-effort expiry of Exo-owned installer payloads only. */ }
    }

    internal static async Task DownloadToAsync(string url, string dest, Action<long, long?>? progress,
        CancellationToken cancellationToken, string? cacheDirectory = null, long? expectedSize = null)
    {
        using var http = ExoHttp.Create(Timeout.InfiniteTimeSpan, new SocketsHttpHandler { AllowAutoRedirect = false });
        await ResumableDownload.DownloadAsync(new Uri(url), dest, cacheDirectory ?? Path.GetDirectoryName(Path.GetFullPath(dest))!,
            (request, ct) => SendFollowingInstallerRedirectsAsync(http, request, ct),
            expectedSize, null, progress, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The installer is the one Exo means to run: the exact file its maker published (its pinned SHA-256, or the SHA-512
    /// Microsoft's .NET release list gives) and signed by its maker. Throws with the reason when it isn't.
    /// </summary>
    internal static void Verify(string path, Runtime runtime, string? sha256, string? sha512)
    {
        if (sha256 is not null && !HashOf(path, SHA256.Create()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{runtime.Name}'s download isn't the file its maker published.");
        if (sha512 is not null && !HashOf(path, SHA512.Create()).Equals(sha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{runtime.Name}'s download doesn't match Microsoft's release list.");
        RequireSigned(path, runtime);
    }

    /// <summary>Signed by its maker: Microsoft itself for Microsoft's runtimes, else the named publisher.</summary>
    internal static void RequireSigned(string path, Runtime runtime)
    {
        var signed = runtime.Publishers.SequenceEqual(MicrosoftOnly)
            ? AuthenticodeVerifier.IsMicrosoft(path)
            : AuthenticodeVerifier.HasPublisher(path, runtime.Publishers);
        if (!signed)
            throw new InvalidDataException($"{runtime.Name}'s installer isn't signed by {(runtime.Publishers.SequenceEqual(MicrosoftOnly) ? "Microsoft" : runtime.Publishers[0])}, so Exo won't run it.");
    }

    internal static string HashOf(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return Convert.ToHexString(algorithm.ComputeHash(stream));
    }
}
