using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Ui;

internal static class UiFormat
{
    public const int MenuWidth = 400;

    public static string StoreLabel(StoreKind store) => store switch
    {
        StoreKind.Local => "Local",
        StoreKind.Steam => "Steam",
        StoreKind.Epic => "Epic",
        StoreKind.Gog => "GOG",
        StoreKind.Riot => "Riot",
        StoreKind.Xbox => "Xbox",
        StoreKind.Ea => "EA",
        StoreKind.Ubisoft => "Ubisoft",
        StoreKind.BattleNet => "Battle.net",
        StoreKind.Amazon => "Amazon",
        StoreKind.Rockstar => "Rockstar",
        StoreKind.Itch => "itch",
        StoreKind.Minecraft => "Minecraft",
        StoreKind.Roblox => "Roblox",
        StoreKind.Paradox => "Paradox",
        StoreKind.Wargaming => "Wargaming",
        _ => store.ToString(),
    };

    public static string StoreLabel(string store)
    {
        if (Enum.TryParse<StoreKind>(store, ignoreCase: true, out var kind))
            return StoreLabel(kind);
        return store;
    }

    public static string Monogram(string title)
    {
        var clean = new string(title.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) return "Ex";
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0].Length <= 2 ? parts[0].ToUpperInvariant() : parts[0][..2].ToUpperInvariant();
        return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
    }

    public static string? Playtime(int? minutes)
    {
        if (minutes is null or <= 0) return null;
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes.Value / 60d;
        return hours < 10 ? $"{hours:0.0}h" : $"{Math.Round(hours)}h";
    }

    public static string? Speed(double? bytesPerSecond)
    {
        if (bytesPerSecond is null or <= 0) return null;
        var mb = bytesPerSecond.Value / (1024 * 1024);
        if (mb >= 1) return $"{mb:0.0} MB/s";
        return $"{Math.Max(1, Math.Round(bytesPerSecond.Value / 1024))} KB/s";
    }

    public static string? Size(long? bytes)
    {
        if (bytes is null or <= 0) return null;
        var gb = bytes.Value / (1024d * 1024d * 1024d);
        if (gb >= 1) return gb < 10 ? $"{gb:0.0} GB" : $"{Math.Round(gb)} GB";
        return $"{Math.Round(bytes.Value / (1024d * 1024d))} MB";
    }

    public static double? VisiblePercent(double? percent)
    {
        if (percent is null or <= 0 || double.IsNaN(percent.Value)) return null;
        return Math.Min(100, percent.Value);
    }

    public static double? DevelopRatio(InstallProgress? progress)
    {
        if (progress is null) return null;
        if (progress.BytesToDownload is > 0 && progress.BytesDownloaded is > 0)
            return Math.Min(1, progress.BytesDownloaded.Value / (double)progress.BytesToDownload.Value);
        var vis = VisiblePercent(progress.Percent);
        return vis is null ? null : vis / 100d;
    }

    public static string NowKicker(NowKind kind) => kind switch
    {
        NowKind.Download => "Downloading",
        NowKind.Playing => "Playing",
        NowKind.Update => "Update",
        NowKind.Recent => "Last launched",
        _ => "",
    };

    public static string ResolvePrimaryAction(GameEntry game)
    {
        if (game.EntitlementState == EntitlementState.NotOwned)
            return "none";
        if (game.EntitlementState == EntitlementState.Unverified && !game.Installed)
            return "none";
        if (game.Installed && (game.UpdateAvailable ||
            string.Equals(game.PrimaryAction, "update", StringComparison.OrdinalIgnoreCase)))
            return "update";
        if (!game.Installed && game.Owned && (game.CanInstall ||
            string.Equals(game.PrimaryAction, "install", StringComparison.OrdinalIgnoreCase)))
            return "install";
        if (game.Installed) return "play";
        return "none";
    }

    public static string PrimaryLabel(GameEntry game, bool transferring, bool running)
    {
        if (transferring) return "Cancel";
        if (running) return "Stop";
        return ResolvePrimaryAction(game) switch
        {
            "install" => "Install",
            "update" => "Update",
            "play" => "Play",
            _ when game.EntitlementState == EntitlementState.NotOwned && BuyUrl(game) is not null => "Buy again",
            _ => "Unavailable",
        };
    }

    public static bool IsLibraryRow(GameEntry game) =>
        !string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<GameEntry> Sort(IReadOnlyList<GameEntry> games, string mode, IReadOnlyList<string> recent)
    {
        var rest = games.Where(IsLibraryRow).ToList();
        int CmpTitle(GameEntry a, GameEntry b) =>
            string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);

        switch (mode)
        {
            case "recent":
            {
                var rank = recent
                    .Select((id, i) => (id, i))
                    .ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
                rest.Sort((a, b) =>
                {
                    var ra = rank.TryGetValue(a.Id, out var ia) ? ia : 9999;
                    var rb = rank.TryGetValue(b.Id, out var ib) ? ib : 9999;
                    if (ra != rb) return ra.CompareTo(rb);
                    var la = a.LastPlayedUtc?.UtcTicks ?? 0;
                    var lb = b.LastPlayedUtc?.UtcTicks ?? 0;
                    if (la != lb) return lb.CompareTo(la);
                    return CmpTitle(a, b);
                });
                break;
            }
            case "played":
                rest.Sort((a, b) => (b.PlaytimeMinutes ?? 0).CompareTo(a.PlaytimeMinutes ?? 0) != 0
                    ? (b.PlaytimeMinutes ?? 0).CompareTo(a.PlaytimeMinutes ?? 0)
                    : CmpTitle(a, b));
                break;
            case "size":
                rest.Sort((a, b) => (b.SizeBytes ?? 0).CompareTo(a.SizeBytes ?? 0) != 0
                    ? (b.SizeBytes ?? 0).CompareTo(a.SizeBytes ?? 0)
                    : CmpTitle(a, b));
                break;
            case "store":
                rest.Sort((a, b) => a.Store.CompareTo(b.Store) != 0 ? a.Store.CompareTo(b.Store) : CmpTitle(a, b));
                break;
            case "favorites":
                rest.Sort((a, b) => b.IsFavorite.CompareTo(a.IsFavorite) != 0
                    ? b.IsFavorite.CompareTo(a.IsFavorite)
                    : CmpTitle(a, b));
                break;
            default:
                rest.Sort(CmpTitle);
                break;
        }

        return rest;
    }

    public static string? BuyUrl(GameEntry game) => Storefront.BuyUrl(game);
}
