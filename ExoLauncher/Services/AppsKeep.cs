using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// What System › Apps never uninstalls because Exo, the games here or Windows itself need it, and why, in the words the
/// page shows. Always: Visual C++ and DirectX (games and apps run on them), GameInput, Xbox's Gaming Services, Windows' app
/// runtimes and Store, and the HEVC and AV1 extensions Exo Capture records with. While it's needed: a store's app while
/// games from it are installed, a game runtime (PhysX, OpenAL, XNA) a game here needs, and Equalizer APO while Lucid is
/// here. Everything else that's needed (.NET, other drivers, Spotify) stays removable with a warning (InstalledPrograms.Cares).
/// </summary>
internal static class AppsKeep
{
    /// <summary>What's needed right now: installed games by store, the runtimes games here need (to a game that does), Lucid.</summary>
    internal sealed record Needs(IReadOnlyDictionary<StoreKind, int> Games, IReadOnlyDictionary<string, string> Runtimes, bool Lucid);

    private static readonly string[] AlwaysNames = ["Microsoft Visual C++", "Microsoft DirectX", "DirectX", "Microsoft GameInput"];
    private static readonly string[] XboxPackages = ["Microsoft.GamingServices", "Microsoft.XboxIdentityProvider"];
    private static readonly string[] WindowsPackages = ["Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Winget.Source",
        "MicrosoftCorporationII.WinAppRuntime", "Microsoft.WindowsAppRuntime", "Microsoft.WebMediaExtensions", "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native"];
    private static readonly string[] CapturePackages = ["Microsoft.HEVCVideoExtension", "Microsoft.HEVCVideoExtensions", "Microsoft.AV1VideoExtension"];
    // The stores whose own app starts their games; Epic, GOG and Amazon games Exo starts itself.
    private static readonly (string Name, StoreKind Store, string Shown)[] Clients =
    [
        ("Steam", StoreKind.Steam, "Steam"), ("EA app", StoreKind.Ea, "EA"), ("Ubisoft Connect", StoreKind.Ubisoft, "Ubisoft"),
        ("Battle.net", StoreKind.BattleNet, "Battle.net"), ("Riot Client", StoreKind.Riot, "Riot"), ("Rockstar Games Launcher", StoreKind.Rockstar, "Rockstar"),
    ];
    // A runtime's uninstall entry, by the start of its name, to its id in the runtimes list (DependencyService's catalog).
    private static readonly (string Name, string Id)[] Runtimes =
        [("NVIDIA PhysX (Legacy)", "physx-legacy"), ("NVIDIA PhysX", "physx"), ("OpenAL", "openal"), ("Microsoft XNA Framework", "xna40")];

    /// <summary>Why <paramref name="program"/> stays; null when it can go.</summary>
    internal static string? Why(InstalledProgram program, Needs needs)
    {
        var name = program.Name;
        var package = program.Store && program.Id.StartsWith("store:", StringComparison.Ordinal) ? program.Id["store:".Length..].Split('_')[0] : null;
        if (AlwaysNames.Any(word => name.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
            return name.Contains("GameInput", StringComparison.OrdinalIgnoreCase) ? "Games read controllers through it" : "Games and apps run on it";
        if (package is not null)
        {
            if (XboxPackages.Any(known => package.Equals(known, StringComparison.OrdinalIgnoreCase))) return "Xbox and Game Pass games need it";
            if (WindowsPackages.Any(known => package.StartsWith(known, StringComparison.OrdinalIgnoreCase))) return "Windows' apps need it";
            if (CapturePackages.Any(known => package.Equals(known, StringComparison.OrdinalIgnoreCase))) return "Exo Capture records with it";
        }
        if (Clients.FirstOrDefault(client => name.Equals(client.Name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } store
            && needs.Games.TryGetValue(store.Store, out var count) && count > 0)
            return count == 1 ? $"Your {store.Shown} game needs it" : $"Your {count} {store.Shown} games need it";
        if (Runtimes.FirstOrDefault(runtime => name.StartsWith(runtime.Name, StringComparison.OrdinalIgnoreCase)) is { Id: { } id }
            && needs.Runtimes.TryGetValue(id, out var game))
            return $"{game} needs it";
        if (needs.Lucid && name.StartsWith("Equalizer APO", StringComparison.OrdinalIgnoreCase)) return "Lucid's EQ runs on it";
        return null;
    }
}
