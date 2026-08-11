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
    /// productId: valorant | league_of_legends | bacon | lion
    /// </summary>
    public static string? FindInstalledProduct(string productId, IEnumerable<string>? rootCandidates = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        var roots = (rootCandidates ?? DefaultRootCandidates)
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

    public static bool IsProductInstalled(string productId, IEnumerable<string>? rootCandidates = null) =>
        FindInstalledProduct(productId, rootCandidates) is not null;

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
            "lion" => ["Teamfight Tactics", "TFT"],
            _ => [productId],
        };

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
            "league_of_legends" => new[]
            {
                Path.Combine(dir, "LeagueClient.exe"),
                Path.Combine(dir, "Game", "League of Legends.exe"),
                Path.Combine(dir, "League of Legends.exe"),
            },
            "bacon" => new[]
            {
                Path.Combine(dir, "LoR.exe"),
                Path.Combine(dir, "Legends of Runeterra.exe"),
            },
            "lion" => new[]
            {
                // TFT ships via League client often — folder presence with any exe is weak signal.
                Path.Combine(dir, "Teamfight Tactics.exe"),
            },
            _ => Array.Empty<string>(),
        };

        if (markers.Any(File.Exists)) return true;

        // Do not infer installation from arbitrary executable trees. Riot leaves
        // bootstrap/patch stubs while downloading; only product-specific markers
        // are strong enough to expose Play or finish an install.
        return false;
    }
}
