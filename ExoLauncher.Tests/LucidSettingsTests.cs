using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Lucid's settings.json as Exo reads and writes it, on a temp copy (never the user's file).
/// Ways it could fail: a save drops or rewrites what Exo doesn't own (Lucid Voice, hotkeys, tone), so Lucid loses its
/// mic tuning, or each game's settings and the game EQ strength, which Exo never changes; a garbled settings.json is
/// copied over the good .bak; a .tmp is left behind; a value Lucid's own load would reset (a time off its half-hour
/// steps, a headphone not in its catalog) gets written; a headphone change keeps a signature tuning that needs Lucid
/// IEMs; two changes made close together lose the first; the headphone list is read wrong out of Lucid's single-file
/// bundle; Lucid's version feed is read differently from Lucid's own updater, so Exo offers an update Lucid wouldn't
/// (or misses one), or a garbled feed reads as a version.
/// </summary>
public sealed class LucidSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-lucid-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    // A trimmed copy of the shape Lucid 3.5 writes.
    private const string Sample = """
        {
          "HotkeyVirtualKey": 122,
          "ActiveProfileId": "north",
          "OptimizeForEsports": true,
          "KeepGameProfileOnAltTab": false,
          "OnlyWhileGaming": true,
          "LastEqState": false,
          "GlobalGameEqStrength": 70,
          "NightShiftEnabled": false,
          "NightShiftStartTime": "21:00:00",
          "NightShiftEndTime": "06:00:00",
          "NightShiftSyncWithWindows": false,
          "NightShiftActive": false,
          "ToggleSoundVolume": 0,
          "AutoDetectionSoundVolume": 0,
          "GameSettings": {
            "Valorant": { "AutoDetectEnabled": true, "UseGameSpecificEq": true },
            "KovaaK's": { "AutoDetectEnabled": true, "UseGameSpecificEq": true }
          },
          "EnableNotifications": false,
          "EnableDiscordRPC": false,
          "AudioSourceType": 0,
          "SelectedHeadphoneId": "",
          "SelectedHeadphoneName": "Lucid IEMs",
          "FavoriteHeadphoneIds": [],
          "HasCompletedAudioSourceOnboarding": true,
          "LucidVoiceEnabled": true,
          "LucidVoiceMicEndpointId": "{0.0.1.00000000}.{0deb347c-90b6-43d6-b549-63e63b1b23a9}",
          "LucidVoiceToneX": -0.7418736765817243,
          "ProfileTone": {}
        }
        """;

    public LucidSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, Sample);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string? Catalog(string id) => id == "astro_a50_x" ? "Astro A50 X" : null;

    private string? Change(LucidSettings.Change change)
    {
        var root = LucidSettings.Load(File_)!;
        var error = LucidSettings.Apply(root, change, Catalog, () => (new TimeSpan(22, 30, 0), new TimeSpan(7, 0, 0)));
        if (error is null) LucidSettings.Save(File_, root);
        return error;
    }

    [Fact]
    public void Lucid_Voice_is_turned_off_and_nothing_else_changes()
    {
        var before = LucidSettings.Load(File_)!;
        before["LucidVoiceEnabled"] = true;
        before["LucidVoicePausedForMicChange"] = true;
        LucidSettings.Save(File_, before);
        Assert.Null(Change(new LucidSettings.Change { NightShift = true }.Then(new LucidSettings.Change { VoiceOff = true })));
        var after = LucidSettings.Load(File_)!;
        Assert.False(after["LucidVoiceEnabled"]!.GetValue<bool>());
        Assert.False(after["LucidVoicePausedForMicChange"]!.GetValue<bool>());
        Assert.True(after["NightShiftEnabled"]!.GetValue<bool>());
        Assert.Equal(before["HotkeyVirtualKey"]!.GetValue<int>(), after["HotkeyVirtualKey"]!.GetValue<int>());
    }

    [Fact]
    public void Lucid_Voice_live_switch_goes_off_and_keeps_the_tuning()
    {
        var off = LucidCompanion.VoiceDspOff("""{ "enabled": true, "engine": "lucidCore", "bodyGainDb": 1.5 }""");
        var root = JsonNode.Parse(off!)!;
        Assert.False(root["enabled"]!.GetValue<bool>());
        Assert.Equal(1.5, root["bodyGainDb"]!.GetValue<double>());
        Assert.Null(LucidCompanion.VoiceDspOff(off!));
        Assert.Null(LucidCompanion.VoiceDspOff("not json"));
    }

    [Fact]
    public void Reads_what_Lucid_wrote()
    {
        var view = LucidSettings.Read(LucidSettings.Load(File_)!);
        Assert.Equal(LucidSettings.LucidIems, view.HeadphoneId);
        Assert.True(view.GameMode);
        Assert.True(view.OnlyWhileGaming);
        Assert.Equal("21:00", view.NightStart);
        Assert.False(view.Notifications);
    }

    [Fact]
    public void A_change_keeps_everything_else_byte_for_byte_and_the_last_good_file_as_bak()
    {
        var before = LucidSettings.Load(File_)!;
        Assert.Null(Change(new LucidSettings.Change { Notifications = true }));
        var after = LucidSettings.Load(File_)!;
        Assert.True((bool)after["EnableNotifications"]!);
        after["EnableNotifications"] = false;
        Assert.True(JsonNode.DeepEquals(before, after));
        // Lucid's own serializer escapes the apostrophe; Exo's save does the same, so Lucid sees no diff.
        Assert.Contains("KovaaK\\u0027s", File.ReadAllText(File_));
        Assert.Contains("-0.7418736765817243", File.ReadAllText(File_));
        Assert.Equal(Sample, File.ReadAllText(File_ + ".bak"));
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void A_garbled_file_never_replaces_the_good_backup()
    {
        File.WriteAllText(File_ + ".bak", Sample);
        File.WriteAllText(File_, "{ not json");
        Assert.Null(LucidSettings.Load(File_));
        LucidSettings.Save(File_, JsonNode.Parse(Sample)!.AsObject());
        Assert.Equal(Sample, File.ReadAllText(File_ + ".bak"));
        Assert.NotNull(LucidSettings.Load(File_));
    }

    [Fact]
    public void The_on_off_key_changes_and_clears_as_Lucids_own_hotkeys_store_it()
    {
        Assert.Equal((0, 122), LucidSettings.Read(LucidSettings.Load(File_)!) is var view ? (view.HotkeyModifiers, view.HotkeyKey) : default);
        Assert.Null(Change(new LucidSettings.Change { Hotkey = (2 | 1, 'L') }));   // Ctrl+Alt+L
        var after = LucidSettings.Load(File_)!;
        Assert.Equal((3, 76), (after["HotkeyModifiers"]!.GetValue<int>(), after["HotkeyVirtualKey"]!.GetValue<int>()));
        Assert.Null(Change(new LucidSettings.Change { Hotkey = (2, 0) }));          // Clear: no key, no modifier left behind
        after = LucidSettings.Load(File_)!;
        Assert.Equal((0, 0), (after["HotkeyModifiers"]!.GetValue<int>(), after["HotkeyVirtualKey"]!.GetValue<int>()));
        Assert.NotNull(Change(new LucidSettings.Change { Hotkey = (0, 300) }));
    }

    [Fact]
    public void The_EQ_is_on_the_output_Lucids_block_names()
    {
        // This PC's config.txt (Oct 27, 2026): Lucid's block names the jack, then gives the rest of the file back to all.
        const string config = "# Equalizer APO Configuration\r\n\r\n# Lucid EQ begin\r\nDevice: {9709e4ab-6969-4817-ad38-1704eab1c079}\r\nInclude: lucid_temp.txt\r\nDevice: all\r\n# Lucid EQ end\r\n";
        var jack = LucidOutput.LucidDevices(config);
        Assert.False(jack?.All);
        Assert.Equal(["{9709e4ab-6969-4817-ad38-1704eab1c079}"], jack?.Guids ?? []);
        Assert.True(LucidOutput.LucidDevices("# Lucid EQ begin\nDevice: all\nInclude: lucid_temp.txt\n# Lucid EQ end")?.All);
        Assert.Null(LucidOutput.LucidDevices("Device: {9709e4ab-6969-4817-ad38-1704eab1c079}\nInclude: other.txt"));   // not Lucid's
        Assert.Null(LucidOutput.LucidDevices("# Lucid EQ begin\nInclude: lucid_temp.txt\n# Lucid EQ end"));              // no output named
    }

    [Fact]
    public void Values_Lucid_would_reset_are_refused()
    {
        Assert.NotNull(Change(new LucidSettings.Change { NightStart = "21:15" }));
        Assert.NotNull(Change(new LucidSettings.Change { NightEnd = "24:00" }));
        Assert.NotNull(Change(new LucidSettings.Change { Headphone = "not_a_headphone" }));
        Assert.Equal(Sample, File.ReadAllText(File_));
    }

    [Fact]
    public void Another_headphone_leaves_the_signature_tuning_as_Lucid_does()
    {
        Assert.Null(Change(new LucidSettings.Change { Headphone = "astro_a50_x" }));
        var root = LucidSettings.Load(File_)!;
        Assert.Equal(1, (int)root["AudioSourceType"]!);
        Assert.Equal("astro_a50_x", (string)root["SelectedHeadphoneId"]!);
        Assert.Equal("Astro A50 X", (string)root["SelectedHeadphoneName"]!);
        Assert.Equal("lucid", (string)root["ActiveProfileId"]!);
        Assert.Null(Change(new LucidSettings.Change { Headphone = LucidSettings.LucidIems }));
        root = LucidSettings.Load(File_)!;
        Assert.Equal(0, (int)root["AudioSourceType"]!);
        Assert.Equal("", (string)root["SelectedHeadphoneId"]!);
        Assert.Equal("Lucid IEMs", (string)root["SelectedHeadphoneName"]!);
    }

    [Fact]
    public void Favorites_night_shift_and_alt_tab_land_where_Lucid_reads_them()
    {
        var games = LucidSettings.Load(File_)!["GameSettings"]!.DeepClone();
        Assert.Null(Change(new LucidSettings.Change
        {
            Favorites = new Dictionary<string, bool> { ["b_one"] = true, ["A_two"] = true },
            KeepOnAltTab = true,
            NightShift = true,
            NightSync = true,
            OnlyWhileGaming = false,
        }));
        var root = LucidSettings.Load(File_)!;
        Assert.Equal(["A_two", "b_one"], root["FavoriteHeadphoneIds"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("22:30:00", (string)root["NightShiftStartTime"]!);
        Assert.Equal("07:00:00", (string)root["NightShiftEndTime"]!);
        Assert.True((bool)root["KeepGameProfileOnAltTab"]!);
        // Each game's settings and the game EQ strength are Lucid's, tuned per game: Exo leaves them as they were.
        Assert.True(JsonNode.DeepEquals(games, root["GameSettings"]));
        Assert.Equal(70, (int)root["GlobalGameEqStrength"]!);
        // Turning Only while gaming off with Game Mode on gives the EQ back, as Lucid's policy does.
        Assert.True((bool)root["LastEqState"]!);
        Assert.True((bool)root["LucidVoiceEnabled"]!);
    }

    [Fact]
    public void Changes_made_together_keep_both()
    {
        var first = new LucidSettings.Change { Notifications = true, Favorites = new Dictionary<string, bool> { ["a"] = true, ["b"] = true } };
        var merged = first.Then(new LucidSettings.Change { Discord = true, Favorites = new Dictionary<string, bool> { ["B"] = false, ["c"] = true } });
        Assert.True(merged.Notifications);
        Assert.True(merged.Discord);
        Assert.True(merged.Favorites!["a"]);
        Assert.False(merged.Favorites!["b"]);
        Assert.True(merged.Favorites!["c"]);
    }

    [Theory]
    [InlineData("6.0.3.0", "6.0.3.0")]
    [InlineData("\uFEFFv6.1\n", "6.1.0.0")]
    [InlineData("  V7  ", "7.0.0.0")]
    [InlineData("6.0.3", "6.0.3.0")]
    [InlineData("<html>", null)]
    [InlineData("", null)]
    public void The_feed_reads_as_Lucids_updater_reads_it(string feed, string? version) =>
        Assert.Equal(version is null ? null : Version.Parse(version), LucidCompanion.Loose(feed));

    [Fact]
    public void A_version_shows_without_its_trailing_zeros()
    {
        Assert.Equal("6.0.3", LucidCompanion.Shown(new Version(6, 0, 3, 0)));
        Assert.Equal("7.0", LucidCompanion.Shown(new Version(7, 0, 0, 0)));
        Assert.Equal("6.0.3.1", LucidCompanion.Shown(new Version(6, 0, 3, 1)));
    }

    [Fact]
    public void Windows_night_light_schedule_is_read_from_its_bytes()
    {
        var data = new byte[40];
        data[23] = 20; data[24] = 30; data[27] = 7; data[28] = 0;
        Assert.Equal((new TimeSpan(20, 30, 0), new TimeSpan(7, 0, 0)), LucidSettings.NightLightSchedule(data));
        Assert.Null(LucidSettings.NightLightSchedule(new byte[10]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_comes_out_of_a_single_file_bundle(bool compress)
    {
        var payload = Encoding.UTF8.GetBytes("the assembly bytes");
        using var bundle = new MemoryStream(Bundle(("Lucid.runtimeconfig.json", "{}"u8.ToArray(), false), ("Lucid.dll", payload, compress)));
        Assert.Equal(payload, LucidCatalog.BundleFile(bundle, "Lucid.dll"));
        Assert.Null(LucidCatalog.BundleFile(bundle, "Other.dll"));
        Assert.Null(LucidCatalog.BundleFile(new MemoryStream(new byte[4096]), "Lucid.dll"));
    }

    [Fact]
    public void The_headphone_list_is_what_Lucids_picker_offers()
    {
        const string json = """
            { "schemaVersion": 2, "devices": [
              { "id": "astro_a50_x", "name": "Astro A50 X", "brand": "Astro", "category": "Gaming Headsets", "enabled": true, "isCalibrated": true, "correctionCost": 1.6, "eqConfig": "Preamp: -3 dB", "aliases": ["a50x"] },
              { "id": "off", "name": "Off One", "brand": "X", "enabled": false, "isCalibrated": true, "eqConfig": "Preamp: 0 dB" },
              { "id": "raw", "name": "Raw One", "brand": "X", "enabled": true, "isCalibrated": false, "eqConfig": "Preamp: 0 dB" },
              { "id": "twin", "name": "Twin", "brand": "", "category": "Earbuds", "enabled": true, "correctionSourceId": "astro_a50_x" },
              { "id": "orphan", "name": "Orphan", "enabled": true, "correctionSourceId": "missing" }
            ] }
            """;
        var list = LucidCatalog.Parse(json);
        Assert.Equal([LucidSettings.LucidIems, "astro_a50_x", "twin"], list.Select(h => h.Id));
        var astro = list[1];
        Assert.Equal("A50 X", astro.Model);
        Assert.Equal("Good", astro.Grade);
        Assert.Contains("a50x", astro.Keywords);
        Assert.Equal("Good", list[2].Grade);
    }

    /// <summary>A .NET single-file bundle (format 6) around the given files.</summary>
    private static byte[] Bundle(params (string Path, byte[] Bytes, bool Compress)[] files)
    {
        byte[] signature =
        [
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
        ];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[1000]); // the host
        var headerSlot = stream.Position;
        writer.Write(0L);
        writer.Write(signature);
        writer.Write(new byte[100]);
        var entries = new List<(long Offset, long Size, long Compressed, string Path)>();
        foreach (var (path, bytes, compress) in files)
        {
            var offset = stream.Position;
            if (compress)
            {
                using var packed = new MemoryStream();
                using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(bytes);
                writer.Write(packed.ToArray());
                entries.Add((offset, bytes.Length, packed.Length, path));
            }
            else
            {
                writer.Write(bytes);
                entries.Add((offset, bytes.Length, 0, path));
            }
        }
        var header = stream.Position;
        writer.Write(6u);
        writer.Write(0u);
        writer.Write(entries.Count);
        writer.Write("bundle-id");
        writer.Write(new byte[40]);
        foreach (var (offset, size, compressed, path) in entries)
        {
            writer.Write(offset);
            writer.Write(size);
            writer.Write(compressed);
            writer.Write((byte)1);
            writer.Write(path);
        }
        stream.Position = headerSlot;
        writer.Write(header);
        return stream.ToArray();
    }
}
