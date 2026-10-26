using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// .NET's newest patches, from Microsoft's own release list (builds.dotnet.microsoft.com/dotnet/release-metadata): the
/// index names each channel's latest release and whether it is still supported, and that release's own record gives each
/// Desktop Runtime installer's URL and SHA-512, which the download is checked against. Read when Game runtimes shows, at
/// most every 6 hours; without it the catalog's versions stand in (<see cref="DotnetChannels"/>).
/// </summary>
public sealed partial class DependencyService
{
    /// <param name="Files">By architecture ("x64", "x86"): the installer's URL and SHA-512.</param>
    internal sealed record DotnetRelease(string Version, bool EndOfSupport, IReadOnlyDictionary<string, (string Url, string Sha512)> Files);

    internal sealed record DotnetChannel(string Channel, string LatestRelease, bool EndOfSupport);

    private const string ReleaseIndex = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";
    private static readonly SemaphoreSlim DotnetGate = new(1, 1);
    private static (IReadOnlyDictionary<string, DotnetRelease> Releases, DateTime At)? _dotnet;

    /// <summary>The index's channels Exo offers (6.0, 8.0, 9.0, 10.0): their latest release and support.</summary>
    internal static IReadOnlyList<DotnetChannel> ParseDotnetIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var channels = new List<DotnetChannel>();
        foreach (var entry in doc.RootElement.GetProperty("releases-index").EnumerateArray())
        {
            var channel = entry.GetProperty("channel-version").GetString();
            var latest = entry.GetProperty("latest-release").GetString();
            if (channel is null || latest is null || !DotnetChannels.Any(c => $"{c.Major}.0" == channel)) continue;
            if (!Regex.IsMatch(latest, @"^\d+\.\d+\.\d+$")) continue; // a preview is never offered
            var phase = entry.TryGetProperty("support-phase", out var p) ? p.GetString() : null;
            channels.Add(new DotnetChannel(channel, latest, string.Equals(phase, "eol", StringComparison.OrdinalIgnoreCase)));
        }
        return channels;
    }

    /// <summary>
    /// One release's record (<c>{channel}/{version}/release.json</c>): the Desktop Runtime's version and its x64 and x86
    /// installers. A file whose URL isn't https on Microsoft's own .NET host, or whose hash isn't a SHA-512, is left out.
    /// </summary>
    internal static DotnetRelease? ParseDotnetRelease(string json, bool endOfSupport)
    {
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement.TryGetProperty("release", out var r) ? r : doc.RootElement;
        if (!release.TryGetProperty("windowsdesktop", out var desktop)) return null;
        var version = desktop.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (version is null || !Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return null;
        var files = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var file in desktop.GetProperty("files").EnumerateArray())
        {
            var rid = file.TryGetProperty("rid", out var ridValue) ? ridValue.GetString() : null;
            var name = file.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var url = file.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            var hash = file.TryGetProperty("hash", out var hashValue) ? hashValue.GetString() : null;
            if (rid is not ("win-x64" or "win-x86") || name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase) || !IsInstallerHost(uri)) continue;
            if (hash is null || !Regex.IsMatch(hash, "^[0-9a-fA-F]{128}$")) continue;
            files[rid[4..]] = (uri.AbsoluteUri, hash.ToUpperInvariant());
        }
        return files.Count == 0 ? null : new DotnetRelease(version, endOfSupport, files);
    }

    /// <summary>
    /// The newest Desktop Runtime of each channel, keyed "8-x64": read from Microsoft at most every 6 hours (10 minutes
    /// after a failure), else what was read before, else nothing (the catalog's own versions stand in).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, DotnetRelease>> DotnetReleasesAsync(CancellationToken ct)
    {
        if (_dotnet is { } fresh && DateTime.UtcNow - fresh.At < TimeSpan.FromHours(6)) return fresh.Releases;
        await DotnetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dotnet is { } again && DateTime.UtcNow - again.At < TimeSpan.FromHours(6)) return again.Releases;
            var releases = new Dictionary<string, DotnetRelease>(StringComparer.Ordinal);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(6));
                using var http = ExoHttp.Create(TimeSpan.FromSeconds(6));
                var channels = ParseDotnetIndex(await http.GetStringAsync(ReleaseIndex, budget.Token).ConfigureAwait(false));
                var reads = channels.Select(async channel =>
                {
                    var json = await http.GetStringAsync(
                        $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel.Channel}/{channel.LatestRelease}/release.json", budget.Token).ConfigureAwait(false);
                    return (channel, release: ParseDotnetRelease(json, channel.EndOfSupport));
                }).ToArray();
                foreach (var (channel, release) in await Task.WhenAll(reads).ConfigureAwait(false))
                {
                    if (release is null) continue;
                    var major = channel.Channel.Split('.')[0];
                    foreach (var arch in release.Files.Keys) releases[$"{major}-{arch}"] = release;
                }
                _dotnet = (releases, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                AppLog.Info("Runtimes: .NET's release list couldn't be read (" + ex.GetType().Name + "); the catalog's versions stand in.");
                var kept = _dotnet?.Releases ?? new Dictionary<string, DotnetRelease>();
                _dotnet = (kept, DateTime.UtcNow - TimeSpan.FromHours(6) + TimeSpan.FromMinutes(10));
            }
            return _dotnet?.Releases ?? new Dictionary<string, DotnetRelease>();
        }
        finally { DotnetGate.Release(); }
    }
}
