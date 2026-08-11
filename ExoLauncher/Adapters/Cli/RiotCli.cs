namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Official RiotClientServices flags only — no CDN scrapers, no invented APIs.
/// </summary>
public static class RiotCli
{
    /// <summary>Fixed catalog product ids (Riot only has a handful).</summary>
    public static readonly IReadOnlyList<(string ProductId, string Title)> FixedCatalog =
    [
        ("valorant", "VALORANT"),
        ("league_of_legends", "League of Legends"),
        ("bacon", "Legends of Runeterra"),
        ("lion", "Teamfight Tactics"),
    ];

    /// <summary>Retail patchline. Riot's local API takes this as a path segment.</summary>
    public const string DefaultPatchline = "live";

    public static string LaunchArgs(string productId, string patchline = "live") =>
        $"--launch-product={productId} --launch-patchline={patchline}";

    public static string UninstallArgs(string productId, string patchline = "live") =>
        $"--uninstall-product={productId} --uninstall-patchline={patchline}";

    /// <summary>Bootstrap installer silent-ish flag used by official Riot installer builds.</summary>
    public static string BootstrapInstallArgs() => "--skip-to-install";

    /// <summary>Processes that are Riot UI chrome — safe to soft-close. Never includes Vanguard or game clients.</summary>
    public static readonly string[] UiProcessNames =
    [
        "Riot Client", // modern Electron host (space in process name)
        "RiotClientServices",
        "RiotClientUx",
        "RiotClientUxRender",
        "RiotClientCrashHandler",
    ];

    /// <summary>Must never be force-closed by Exo.</summary>
    public static readonly string[] ProtectedProcessNames =
    [
        "vgk",
        "vgc",
        "vgm",
    ];

    public static bool IsProtectedProcess(string processName) =>
        ProtectedProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownProduct(string productId) =>
        FixedCatalog.Any(c => string.Equals(c.ProductId, productId, StringComparison.OrdinalIgnoreCase));
}
