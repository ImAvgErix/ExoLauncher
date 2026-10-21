namespace ExoLauncher.Adapters;

/// <summary>
/// Pure filesystem probes for Riot product install status.
/// No process starts — unit tests drive these on real or fixture paths.
/// </summary>
public static class RiotInstallProbe
{
    public static readonly IReadOnlyList<string> DefaultRootCandidates =
    [
        @"C:\Riot Games",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games"),
    ];

    /// <summary>
    /// Returns the best install directory for a Riot product, or null if not installed.
    /// productId: a <see cref="Cli.RiotCli.FixedCatalog"/> id. Without explicit
    /// roots, Riot's own product_settings.yaml decides first, so installs on
    /// other drives or in custom folders are found; folder-name guessing is the fallback.
    /// </summary>
    public static string? FindInstalledProduct(string productId, IEnumerable<string>? rootCandidates = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        Riot.RiotProductMetadata.ProductSettings? settings = null;
        if (rootCandidates is null)
        {
            settings = Riot.RiotProductMetadata.ReadSettings(productId);
            var fromRiot = FromSettings(productId, settings);
            if (fromRiot is not null) return fromRiot;
        }

        var candidates = rootCandidates ?? DefaultRootCandidates;
        if (settings?.InstallRoot is { } riotRoot)
            candidates = candidates.Prepend(riotRoot);
        var roots = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            var hit = FindInRoot(root, productId);
            if (hit is not null) return hit;
        }

        return null;
    }

    public static string? FindRiotClientServices(IEnumerable<string>? rootCandidates = null)
    {
        var roots = (rootCandidates ?? DefaultRootCandidates)
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var direct = Path.Combine(root, "Riot Client", "RiotClientServices.exe");
            if (File.Exists(direct)) return direct;
        }

        // Registry uninstall keys sometimes point at product dirs; walk parents for client.
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is not null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    if (!subName.Contains("Riot", StringComparison.OrdinalIgnoreCase) &&
                        !subName.Contains("valorant", StringComparison.OrdinalIgnoreCase) &&
                        !subName.Contains("league", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var sub = key.OpenSubKey(subName);
                    var loc = sub?.GetValue("InstallLocation") as string
                              ?? sub?.GetValue("DisplayIcon") as string;
                    if (string.IsNullOrWhiteSpace(loc)) continue;
                    var dir = Directory.Exists(loc) ? loc : Path.GetDirectoryName(loc);
                    while (!string.IsNullOrWhiteSpace(dir))
                    {
                        var candidate = Path.Combine(dir, "Riot Client", "RiotClientServices.exe");
                        if (File.Exists(candidate)) return candidate;
                        // Also: ...\Riot Games\Riot Client
                        var parent = Directory.GetParent(dir)?.FullName;
                        if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                            break;
                        dir = parent;
                        if (dir.Length < 3) break;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    /// <summary>
    /// The install folder Riot recorded. VALORANT and TFT record their
    /// <c>live</c> subfolder; the product folder above it is the row's path,
    /// as folder guessing has always reported it.
    /// </summary>
    internal static string? FromSettings(string productId, Riot.RiotProductMetadata.ProductSettings? settings)
    {
        var full = settings?.InstallFullPath;
        if (string.IsNullOrWhiteSpace(full)) return null;
        var parent = Path.GetDirectoryName(full);
        if (string.Equals(Path.GetFileName(full), "live", StringComparison.OrdinalIgnoreCase) &&
            parent is not null && LooksInstalled(productId, parent))
            return parent;
        return LooksInstalled(productId, full) ? full : null;
    }

    private static string? FindInRoot(string root, string productId)
    {
        var folderNames = ProductFolderNames(productId);
        foreach (var folder in folderNames)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            if (LooksInstalled(productId, dir))
                return dir;
        }

        return null;
    }

    public static IReadOnlyList<string> ProductFolderNames(string productId) =>
        productId.ToLowerInvariant() switch
        {
            "valorant" => ["VALORANT", "valorant"],
            "league_of_legends" => ["League of Legends", "LeagueOfLegends"],
            "bacon" => ["Legends of Runeterra", "LoR"],
            "lion" => ["2XKO", "Lion"],
            "teamfighttactics" => ["Teamfight Tactics"],
            _ => [productId],
        };

    private static string[] LeagueMarkers(string dir) =>
    [
        Path.Combine(dir, "LeagueClient.exe"),
        Path.Combine(dir, "Game", "League of Legends.exe"),
        Path.Combine(dir, "League of Legends.exe"),
    ];

    /// <summary>True when directory contains launchable product markers.</summary>
    public static bool LooksInstalled(string productId, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;

        var markers = productId.ToLowerInvariant() switch
        {
            "valorant" => new[]
            {
                Path.Combine(dir, "live", "VALORANT.exe"),
                Path.Combine(dir, "VALORANT.exe"),
                Path.Combine(dir, "live", "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"),
            },
            "league_of_legends" => LeagueMarkers(dir),
            // TFT ships its own client; older installs played it inside
            // League, and Riot's metadata then points TFT at League's folder.
            "teamfighttactics" => new[]
            {
                Path.Combine(dir, "TFTClient.exe"),
                Path.Combine(dir, "live", "TFTClient.exe"),
                Path.Combine(dir, "TFT", "Binaries", "Win64", "TFTClient-Win64-Shipping.exe"),
            }.Concat(LeagueMarkers(dir)).ToArray(),
            "bacon" => new[]
            {
                Path.Combine(dir, "LoR.exe"),
                Path.Combine(dir, "Legends of Runeterra.exe"),
            },
            "lion" => new[]
            {
                Path.Combine(dir, "2XKO.exe"),
                Path.Combine(dir, "Lion.exe"),
                Path.Combine(dir, "live", "2XKO.exe"),
            },
            _ => Array.Empty<string>(),
        };

        if (markers.Any(File.Exists)) return true;

        // Do not infer installation from arbitrary executable trees. Riot leaves
        // bootstrap/patch stubs while downloading; only product-specific markers
        // are strong enough to expose Play or finish an install.
        return false;
    }

    /// <summary>
    /// Windows Add/Remove Programs size for a Riot product, in bytes. This is
    /// the game's own uninstall key — Vanguard and Riot Client are separate.
    /// </summary>
    public static long? TryReadInstallSizeBytes(string productId)
    {
        var keyName = ProductUninstallKey(productId);
        if (keyName is null) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName);
            return ReadEstimatedSizeBytes(key);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ProductUninstallKey(string productId) =>
        Cli.RiotCli.IsKnownProduct(productId.Trim())
            ? "Riot Game " + productId.Trim().ToLowerInvariant() + ".live"
            : null;

    internal static long? ReadEstimatedSizeBytes(Microsoft.Win32.RegistryKey? key)
    {
        if (key is null) return null;
        var raw = key.GetValue("EstimatedSize");
        long kb = raw switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => ul > long.MaxValue ? 0 : (long)ul,
            _ => 0,
        };
        if (kb <= 0) return null;
        return kb * 1024L;
    }
}
