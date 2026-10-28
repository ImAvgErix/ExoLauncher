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
    /// <summary>Microsoft's .NET release list, read at most every 6 hours and retried as every version check is (<see cref="LatestCache{T}"/>).</summary>
    private static readonly LatestCache<Dictionary<string, DotnetRelease>> Dotnet = new(".NET's release list", TimeSpan.FromHours(6));

    /// <summary>What was read of .NET's releases last, without asking (null: nothing yet; the catalog's versions stand in).</summary>
    private static IReadOnlyDictionary<string, DotnetRelease>? DotnetKept() => Dotnet.Kept("releases");

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
        return ReleaseOf(doc.RootElement.TryGetProperty("release", out var r) ? r : doc.RootElement, endOfSupport);
    }

    /// <summary>
    /// The same from a channel's whole list (<c>{channel}/releases.json</c>), for a release Microsoft keeps no record of its
    /// own for (.NET 6's last, 6.0.36): the entry whose release-version is <paramref name="version"/>.
    /// </summary>
    internal static DotnetRelease? ParseDotnetReleases(string json, string version, bool endOfSupport)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.GetProperty("releases").EnumerateArray())
            if (release.TryGetProperty("release-version", out var v) && v.GetString() == version) return ReleaseOf(release, endOfSupport);
        return null;
    }

    private static DotnetRelease? ReleaseOf(JsonElement release, bool endOfSupport)
    {
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
    /// The newest Desktop Runtime of each channel, keyed "8-x64": read from Microsoft at most every 6 hours (soon again
    /// after a failure), else what was read before, else nothing (the catalog's own versions stand in).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, DotnetRelease>> DotnetReleasesAsync(CancellationToken ct) =>
        await Dotnet.GetAsync("releases", async lookup =>
        {
            var releases = new Dictionary<string, DotnetRelease>(StringComparer.Ordinal);
            using var http = ExoHttp.Create(TimeSpan.FromSeconds(15));
            var channels = ParseDotnetIndex(await http.GetStringAsync(ReleaseIndex, lookup).ConfigureAwait(false));
            var reads = channels.Select(channel => ReadChannelAsync(http, channel, lookup)).ToArray();
            foreach (var (channel, release) in await Task.WhenAll(reads).ConfigureAwait(false))
            {
                if (release is null) continue;
                var major = channel.Channel.Split('.')[0];
                foreach (var arch in release.Files.Keys) releases[$"{major}-{arch}"] = release;
            }
            return releases;
        }, ct).ConfigureAwait(false) ?? new Dictionary<string, DotnetRelease>();

    /// <summary>
    /// One channel's latest release: its own record, else the channel's whole list. A channel that can't be read is left to
    /// the catalog alone, so one missing file never fails every .NET (6.0.36 has no record of its own and failed them all).
    /// </summary>
    private static async Task<(DotnetChannel Channel, DotnetRelease? Release)> ReadChannelAsync(HttpClient http, DotnetChannel channel, CancellationToken ct)
    {
        const string Metadata = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/";
        try
        {
            using var response = await http.GetAsync($"{Metadata}{channel.Channel}/{channel.LatestRelease}/release.json", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (channel, ParseDotnetRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), channel.EndOfSupport));
            var all = await http.GetStringAsync($"{Metadata}{channel.Channel}/releases.json", ct).ConfigureAwait(false);
            return (channel, ParseDotnetReleases(all, channel.LatestRelease, channel.EndOfSupport));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info($"Runtimes: .NET {channel.Channel}'s release couldn't be read ({ex.GetType().Name}); the catalog's version stands in.");
            return (channel, null);
        }
    }
}
