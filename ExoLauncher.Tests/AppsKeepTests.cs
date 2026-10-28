using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What System › Apps never uninstalls: getting it wrong lets a player remove what their games or Exo need, with nothing
/// said until a game won't start.
/// </summary>
public sealed class AppsKeepTests
{
    private static InstalledProgram Desktop(string name) =>
        new("HKLM:{x}", name, null, null, null, null, null, null, Store: false, Machine: true, "MsiExec.exe /X{x}", null, null, true, null, null, null);

    private static InstalledProgram Package(string family) =>
        new("store:" + family, family, null, null, null, null, null, null, Store: true, Machine: false, null, null, family, true, null, null, null);

    private static readonly AppsKeep.Needs Nothing = new(new Dictionary<StoreKind, int>(), new Dictionary<string, string>(), Lucid: false);

    [Fact]
    public void WhatGamesAndWindowsRunOnAlwaysStays()
    {
        Assert.Equal("Games and apps run on it", AppsKeep.Why(Desktop("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211"), Nothing));
        Assert.Equal("Games read controllers through it", AppsKeep.Why(Desktop("Microsoft GameInput"), Nothing));
        Assert.Equal("Xbox and Game Pass games need it", AppsKeep.Why(Package("Microsoft.GamingServices_8wekyb3d8bbwe"), Nothing));
        Assert.Equal("Exo Capture records with it", AppsKeep.Why(Package("Microsoft.HEVCVideoExtension_8wekyb3d8bbwe"), Nothing));
        Assert.Null(AppsKeep.Why(Package("Microsoft.HEIFImageExtension_8wekyb3d8bbwe"), Nothing));   // not one Exo uses
        Assert.Null(AppsKeep.Why(Desktop("Discord"), Nothing));
    }

    [Fact]
    public void BloatwareIsWhatNothingNeeds()
    {
        Assert.Equal("Comes with Windows; nothing needs it", AppsBloat.Why(Package("Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe")));
        Assert.Equal("Windows installed it as an ad", AppsBloat.Why(Package("king.com.CandyCrushSaga_kgqvnymyfvs32")));
        Assert.Equal("A trial your PC came with", AppsBloat.Why(Desktop("McAfee LiveSafe")));
        Assert.Null(AppsBloat.Why(Package("SpotifyAB.SpotifyMusic_zpdnekdrzrea0") with { Care = InstalledPrograms.Cares.ExoUses }));   // Exo uses it
        Assert.Null(AppsBloat.Why(Package("ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr")));                                             // a game
        Assert.Null(AppsBloat.Why(Package("Microsoft.ZuneMusic_8wekyb3d8bbwe")));                                                  // Media Player plays your music
    }

    [Fact]
    public void LastOpenedIsReadAsWindowsWritesIt()
    {
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", AppsUsage.Rot13("Zvpebfbsg.JvaqbjfPnyphyngbe_8jrxlo3q8oojr!Ncc"));
        var entry = new byte[72];
        BitConverter.GetBytes(new DateTime(2025, 3, 3, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(entry, 60);
        Assert.Equal(new DateTime(2025, 3, 3, 12, 0, 0, DateTimeKind.Utc), AppsUsage.LastRun(entry));
        Assert.Null(AppsUsage.LastRun(new byte[72]));   // counted, never run
        Assert.Null(AppsUsage.LastRun(new byte[16]));   // Windows XP's shape
        var paths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Program Files\Tool\bin\tool.exe"] = new(2025, 1, 1), [@"C:\Program Files\Tool\toolview.exe"] = new(2025, 6, 1), [@"C:\Program Files\Toolbox\x.exe"] = new(2026, 1, 1),
        };
        var exes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["TOOL.EXE"] = new(2025, 8, 1), ["TOOLUPDATER.EXE"] = new(2026, 2, 1) };
        var tool = Desktop("Tool") with { Location = @"C:\Program Files\Tool" };
        Assert.Equal(new DateTime(2025, 6, 1), AppsUsage.LastOpened(tool, new(paths, new Dictionary<string, DateTime>())));   // its newest exe; Toolbox is another folder
        // Prefetch says tool.exe ran later; its updater runs by itself, so it doesn't count.
        Assert.Equal(new DateTime(2025, 8, 1), AppsUsage.LastOpened(tool, new(paths, exes), _ => ["tool.exe", "toolupdater.exe"]));
        Assert.Equal("STEAM.EXE", AppsUsage.PrefetchExe("STEAM.EXE-1A2B3C4D.pf"));
        Assert.Null(AppsUsage.PrefetchExe("Layout.ini"));
    }

    [Fact]
    public void AStoreOrRuntimeStaysOnlyWhileAGameHereNeedsIt()
    {
        var needs = new AppsKeep.Needs(new Dictionary<StoreKind, int> { [StoreKind.Steam] = 12, [StoreKind.Ea] = 1 },
            new Dictionary<string, string> { ["physx-legacy"] = "Batman: Arkham Asylum" }, Lucid: true);
        Assert.Equal("Your 12 Steam games need it", AppsKeep.Why(Desktop("Steam"), needs));
        Assert.Equal("Your EA game needs it", AppsKeep.Why(Desktop("EA app"), needs));
        Assert.Null(AppsKeep.Why(Desktop("Ubisoft Connect"), needs));                                // no Ubisoft game here
        Assert.Equal("Batman: Arkham Asylum needs it", AppsKeep.Why(Desktop("NVIDIA PhysX (Legacy)"), needs));
        Assert.Null(AppsKeep.Why(Desktop("NVIDIA PhysX System Software 9.23.1019"), needs));          // the other PhysX: no game asks
        Assert.Equal("Lucid's EQ runs on it", AppsKeep.Why(Desktop("Equalizer APO"), needs));
        Assert.Null(AppsKeep.Why(Desktop("Equalizer APO"), Nothing));                                 // without Lucid it can go
        Assert.Null(AppsKeep.Why(Desktop("Steam"), Nothing));
    }
}
