using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Adapters;
using Kept = ExoLauncher.Adapters.SteamShortcuts.Kept;
using Wanted = ExoLauncher.Adapters.SteamShortcuts.Wanted;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could get Steam's non-Steam games wrong for a player:
/// - a write that loses another shortcut, a field Steam keeps (play time, tags), or a byte of either;
/// - taking out or rewriting a shortcut the player made, or one Exo made that the player changed since;
/// - putting back a shortcut the player deleted in Steam;
/// - a moved game whose shortcut still starts the old folder;
/// - an id that isn't the one Steam gives the shortcut, so steam://rungameid starts nothing.
/// </summary>
public sealed class SteamShortcutsTests
{
    private static readonly Dictionary<string, Kept> NoRecord = [];
    private static readonly Wanted Hades = new("gog:hades", "Hades", @"D:\GOG\Hades\x64\Hades.exe", @"D:\GOG\Hades\x64", "", @"D:\GOG\Hades\x64\Hades.exe");

    private static byte[] Str(string key, string value) => [0x01, .. Encoding.UTF8.GetBytes(key), 0, .. Encoding.UTF8.GetBytes(value), 0];
    private static byte[] Int(string key, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return [0x02, .. Encoding.UTF8.GetBytes(key), 0, .. b];
    }
    private static byte[] Sec(string key, params byte[][] body) => [0x00, .. Encoding.UTF8.GetBytes(key), 0, .. body.SelectMany(x => x), 0x08];

    /// <summary>A file as Steam writes it, with one shortcut the player made (and fields Exo never writes).</summary>
    private static byte[] PlayersFile() =>
    [
        .. Sec("shortcuts",
            Sec("0",
                Int("appid", 0x9A1B2C3D), Str("AppName", "Emulator"), Str("Exe", "\"D:\\Emu\\emu.exe\""), Str("StartDir", "\"D:\\Emu\\\""),
                Str("icon", ""), Str("LaunchOptions", "-fullscreen"), Int("IsHidden", 0), Int("LastPlayTime", 1_761_000_000),
                Str("sortas", "emu"), Sec("tags", Str("0", "Favorites")))),
        0x08,
    ];

    private static List<SteamShortcuts.Node> Entries(byte[] file) => SteamShortcuts.Read(file)[0].Entries!;
    private static SteamShortcuts.Node Entry(byte[] file, uint appId) => Entries(file).Single(e => SteamShortcuts.IdOf(e) == appId);
    private static string? Field(SteamShortcuts.Node entry, string key) => entry.Entries!.FirstOrDefault(f => f.Key == key)?.Value;

    [Fact]
    public void AFileReadsAndWritesBackByteForByte()
    {
        var file = PlayersFile();
        Assert.Equal(file, SteamShortcuts.Write(SteamShortcuts.Read(file)));
    }

    [Fact]
    public void AddingExosShortcutKeepsThePlayersWhole()
    {
        var file = PlayersFile();
        var (next, record, dropped) = SteamShortcuts.Plan(file, [Hades], NoRecord);
        Assert.NotNull(next);
        Assert.Empty(dropped);
        var entries = Entries(next);
        Assert.Equal(["0", "1"], entries.Select(e => e.Key));
        // The player's entry is the same bytes as before.
        Assert.Equal(SteamShortcuts.Write([Entries(file)[0]]), SteamShortcuts.Write([entries[0]]));
        var exos = Entry(next, record["gog:hades"].AppId);
        Assert.Equal("Hades", Field(exos, "AppName"));
        Assert.Equal("\"D:\\GOG\\Hades\\x64\\Hades.exe\"", Field(exos, "Exe"));
        Assert.Equal("\"D:\\GOG\\Hades\\x64\"", Field(exos, "StartDir"));
        // Already there: nothing to write the second time.
        Assert.Null(SteamShortcuts.Plan(next, [Hades], record).File);
    }

    [Fact]
    public void ANewFileHasOnlyExosShortcut()
    {
        var (next, record, _) = SteamShortcuts.Plan(null, [Hades], NoRecord);
        Assert.Single(Entries(next!));
        Assert.Equal(SteamShortcuts.AppId("\"D:\\GOG\\Hades\\x64\\Hades.exe\"", "Hades"), record["gog:hades"].AppId);
    }

    [Fact]
    public void TurningItOffTakesOutOnlyWhatExoAdded()
    {
        var (withExos, record, _) = SteamShortcuts.Plan(PlayersFile(), [Hades], NoRecord);
        var (after, next, dropped) = SteamShortcuts.Plan(withExos, [], record);
        Assert.Equal(PlayersFile(), after);
        Assert.Empty(next);
        Assert.Empty(dropped);
    }

    [Fact]
    public void AMovedGameGetsItsShortcutUpdatedInPlace()
    {
        var (withExos, record, _) = SteamShortcuts.Plan(PlayersFile(), [Hades], NoRecord);
        var moved = Hades with { Exe = @"E:\Games\Hades\x64\Hades.exe", StartDir = @"E:\Games\Hades\x64", LaunchOptions = "-novsync" };
        var (after, next, dropped) = SteamShortcuts.Plan(withExos, [moved], record);
        Assert.Empty(dropped);
        Assert.Equal(record["gog:hades"].AppId, next["gog:hades"].AppId);   // Steam's id, so its play time and layout, kept
        var exos = Entry(after!, next["gog:hades"].AppId);
        Assert.Equal("\"E:\\Games\\Hades\\x64\\Hades.exe\"", Field(exos, "Exe"));
        Assert.Equal("-novsync", Field(exos, "LaunchOptions"));
        Assert.Equal(2, Entries(after!).Count);
    }

    [Fact]
    public void AShortcutThePlayerChangedOrDeletedIsTheirs()
    {
        var (withExos, record, _) = SteamShortcuts.Plan(PlayersFile(), [Hades], NoRecord);
        var top = SteamShortcuts.Read(withExos!);
        top[0].Entries!.Single(e => SteamShortcuts.IdOf(e) == record["gog:hades"].AppId).Entries!.Single(f => f.Key == "LaunchOptions").Value = "-windowed";
        var changed = SteamShortcuts.Write(top);

        // Turned off in Exo: the changed shortcut stays.
        Assert.Null(SteamShortcuts.Plan(changed, [], record).File);
        // Still on in Exo: the game is dropped, and the shortcut is neither rewritten nor added again.
        var (same, next, dropped) = SteamShortcuts.Plan(changed, [Hades], record);
        Assert.Null(same);
        Assert.Empty(next);
        Assert.Equal(["gog:hades"], dropped);

        var (deleted, afterDelete, droppedAfterDelete) = SteamShortcuts.Plan(PlayersFile(), [Hades], record);
        Assert.Null(deleted);
        Assert.Empty(afterDelete);
        Assert.Equal(["gog:hades"], droppedAfterDelete);
    }

    [Fact]
    public void IdsAreTheOnesSteamUses()
    {
        var appId = SteamShortcuts.AppId("\"C:\\Games\\game.exe\"", "Game");
        Assert.True((appId & 0x80000000) != 0);
        Assert.Equal(Helpers.Crc32.Of(Encoding.UTF8.GetBytes("\"C:\\Games\\game.exe\"Game")) | 0x80000000, appId);
        Assert.Equal(((ulong)appId << 32) | 0x02000000, SteamShortcuts.GameId(appId));
        // Two games of one name and program get two shortcuts.
        var (_, record, _) = SteamShortcuts.Plan(null, [Hades, Hades with { GameId = "local:hades" }], NoRecord);
        Assert.NotEqual(record["gog:hades"].AppId, record["local:hades"].AppId);
    }

    [Fact]
    public void AFileExoDoesntKnowIsNeverWritten()
    {
        byte[] odd = [0x00, .. "shortcuts"u8, 0, 0x05, .. "w"u8, 0, 0x41, 0, 0, 0, 0x08, 0x08];
        Assert.Throws<InvalidDataException>(() => SteamShortcuts.Plan(odd, [Hades], NoRecord));
        Assert.Throws<InvalidDataException>(() => SteamShortcuts.Read([0x00, .. "shortcuts"u8, 0, 0x01, .. "AppName"u8, 0, .. "x"u8]));
    }
}
