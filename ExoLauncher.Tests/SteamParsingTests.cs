using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam's local files can be misread:
/// - localconfig.vdf: the two-week counter wins over the lifetime one; a legacy "Playtime" is ignored;
///   an app with neither playtime nor LastPlayed is listed; merging a second account file lowers minutes
///   or moves LastPlayed back; an unclosed block throws;
/// - apptickets: entries outside the section count; a malformed line counts;
/// - appinfo.vdf (v41): a wrong magic or a string table offset outside the file throws instead of
///   returning nothing; names outside "common" are taken; a "0" parent counts as a parent; a record
///   whose size runs past the table is read.
/// </summary>
public sealed class SteamParsingTests
{
    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
          "Software" { "Valve" { "Steam" { "apps"
          {
            "730"
            {
              "LastPlayed"   "1700000000"
              "Playtime2wks" "90"
              "PlaytimeForever" "1200"
            }
            "440"
            {
              "Playtime"   "75"
            }
            "570"
            {
              "cloud" { "last_sync_state" "synchronized" }
            }
          } } } }
        }
        """;

    [Fact]
    public void ReadsLifetimeMinutesAndLastPlayed()
    {
        var map = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal);
        SteamPlaytime.MergeFile(map, LocalConfig);
        Assert.Equal(1200, map["730"].Minutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), map["730"].LastPlayedUtc);
        Assert.Equal(75, map["440"].Minutes);
        Assert.Null(map["440"].LastPlayedUtc);
        Assert.False(map.ContainsKey("570"));
    }

    [Fact]
    public void MergingKeepsTheLargerMinutesAndTheLaterDate()
    {
        var map = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal);
        SteamPlaytime.MergeFile(map, LocalConfig);
        SteamPlaytime.MergeFile(map, """
            "apps" { "730" { "PlaytimeForever" "100" "LastPlayed" "1800000000" } }
            """);
        Assert.Equal(1200, map["730"].Minutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), map["730"].LastPlayedUtc);
    }

    [Fact]
    public void UnclosedBlockIsSkipped()
    {
        var map = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal);
        SteamPlaytime.MergeFile(map, "\"730\" { \"PlaytimeForever\" \"5\"");
        Assert.Empty(map);
    }

    [Fact]
    public void ReadsAppTicketsSectionOnly()
    {
        const string text = """
            "Steam"
            {
              "730" "deadbeef"
              "apptickets"
              {
                "440"   "0a1b2c"
                "570"   "ffff"
                "bad"   "ffff"
                "620"   "not-hex"
              }
            }
            """;
        Assert.Equal(["440", "570"], SteamPlaytime.ParseAppTickets(text).Order());
        Assert.Empty(SteamPlaytime.ParseAppTickets(""));
        Assert.Empty(SteamPlaytime.ParseAppTickets("\"apptickets\" {"));
    }

    /// <summary>A v41 appinfo.vdf: header, app records with binary KeyValues keyed by string-table index, then the table.</summary>
    private static byte[] AppInfo(params (uint AppId, byte[] Kv)[] apps)
    {
        string[] table = ["appinfo", "common", "name", "type", "parent", "extended"];
        var body = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); body.Write(b); }
        body.Write(new byte[16]);
        foreach (var (appId, kv) in apps)
        {
            U32(appId);
            U32((uint)(60 + kv.Length));
            body.Write(new byte[60]);
            body.Write(kv);
        }
        U32(0);
        var tableOffset = body.Length;
        U32((uint)table.Length);
        foreach (var s in table) { body.Write(Encoding.UTF8.GetBytes(s)); body.WriteByte(0); }
        var data = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x07564429);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), tableOffset);
        return data;
    }

    private sealed class Kv
    {
        private readonly MemoryStream _s = new();
        private Kv Key(byte kind, int key) { _s.WriteByte(kind); _s.Write(BitConverter.GetBytes(key)); return this; }
        internal Kv Open(int key) => Key(0, key);
        internal Kv Str(int key, string value) { Key(1, key); _s.Write(Encoding.UTF8.GetBytes(value)); _s.WriteByte(0); return this; }
        internal Kv Int(int key, uint value) { Key(2, key); _s.Write(BitConverter.GetBytes(value)); return this; }
        internal Kv Close() { _s.WriteByte(8); return this; }
        internal byte[] Bytes => _s.ToArray();
    }

    // String-table indexes: 0 appinfo, 1 common, 2 name, 3 type, 4 parent, 5 extended.
    [Fact]
    public void ReadsNamesTypesAndParentsFromCommon()
    {
        var data = AppInfo(
            (730, new Kv().Open(0).Open(1).Str(2, " Counter-Strike 2 ").Str(3, "Game").Close()
                .Open(5).Str(2, "not this").Close().Close().Bytes),
            (731, new Kv().Open(0).Open(1).Str(2, "CS2 Soundtrack").Str(3, "Music").Int(4, 730).Close().Close().Bytes),
            (732, new Kv().Open(0).Open(1).Str(2, "Orphan").Str(4, "0").Close().Close().Bytes),
            (733, new Kv().Open(0).Open(5).Str(2, "Outside common").Close().Close().Bytes));
        var names = SteamAppInfoNames.Parse(data);
        Assert.Equal(new SteamAppInfoNames.Entry("Counter-Strike 2", "Game", ""), names["730"]);
        Assert.Equal(new SteamAppInfoNames.Entry("CS2 Soundtrack", "Music", "730"), names["731"]);
        Assert.Equal("", names["732"].ParentAppId);
        Assert.False(names.ContainsKey("733"));
    }

    [Fact]
    public void RejectsWrongMagicAndBadTableOffset()
    {
        var good = AppInfo((730, new Kv().Open(0).Open(1).Str(2, "X").Close().Close().Bytes));
        Assert.Single(SteamAppInfoNames.Parse(good));

        var magic = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(magic, 0x07564428);
        Assert.Empty(SteamAppInfoNames.Parse(magic));

        var offset = (byte[])good.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(offset.AsSpan(8), good.Length + 10);
        Assert.Empty(SteamAppInfoNames.Parse(offset));

        Assert.Empty(SteamAppInfoNames.Parse(new byte[8]));
    }

    [Fact]
    public void RecordRunningPastTheTableStopsTheWalk()
    {
        var data = AppInfo((730, new Kv().Open(0).Open(1).Str(2, "X").Close().Close().Bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1_000_000);
        Assert.Empty(SteamAppInfoNames.Parse(data));
    }
}
