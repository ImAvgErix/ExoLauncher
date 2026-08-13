using System.Text;
using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamAppInfoNamesTests
{
    [Fact]
    public void Parse_ReadsV41CommonNameAndSkipsDlc()
    {
        var bytes = BuildV41(
            (730, "Counter-Strike 2", "game"),
            (123, "Some DLC", "dlc"),
            (1085660, "Destiny 2", "Game"));

        var names = SteamAppInfoNames.Parse(bytes);

        Assert.Equal("Counter-Strike 2", names["730"].Name);
        Assert.True(names["730"].IsPlayableTitle);
        Assert.Equal("Destiny 2", names["1085660"].Name);
        Assert.True(names.ContainsKey("123"));
        Assert.False(names["123"].IsPlayableTitle);
    }

    [Fact]
    public void Parse_LiveSteamAppInfo_IncludesInstalledGames()
    {
        var steam = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(steam)) return;
        var path = Path.Combine(steam.Replace('/', Path.DirectorySeparatorChar), "appcache", "appinfo.vdf");
        if (!File.Exists(path)) return;

        var names = SteamAppInfoNames.Load(path);
        Assert.True(names.Count > 0);
        Assert.True(names.TryGetValue("730", out var cs2), "CS2 should be in this machine's appinfo.");
        Assert.Contains("Counter-Strike", cs2.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(cs2.IsPlayableTitle);
    }

    internal static byte[] BuildV41(params (uint AppId, string Name, string Type)[] apps)
    {
        var strings = new[] { "common", "name", "type" };
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
        foreach (var app in apps)
        {
            using var kv = new MemoryStream();
            using var kvWriter = new BinaryWriter(kv, Encoding.UTF8, leaveOpen: true);
            kvWriter.Write((byte)0);
            kvWriter.Write(0u); // common
            kvWriter.Write((byte)1);
            kvWriter.Write(1u); // name
            kvWriter.Write(Encoding.UTF8.GetBytes(app.Name));
            kvWriter.Write((byte)0);
            kvWriter.Write((byte)1);
            kvWriter.Write(2u); // type
            kvWriter.Write(Encoding.UTF8.GetBytes(app.Type));
            kvWriter.Write((byte)0);
            kvWriter.Write((byte)8);
            kvWriter.Write((byte)8);
            var kvBytes = kv.ToArray();

            writer.Write(app.AppId);
            writer.Write(60 + kvBytes.Length);
            writer.Write(new byte[60]);
            writer.Write(kvBytes);
        }
        writer.Write(0u); // end of apps

        var appsBytes = body.ToArray();
        var tableOffset = 16 + appsBytes.Length;
        using var file = new MemoryStream();
        using var fileWriter = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        fileWriter.Write(0x07564429u);
        fileWriter.Write(1u);
        fileWriter.Write((long)tableOffset);
        fileWriter.Write(appsBytes);
        fileWriter.Write(strings.Length);
        foreach (var s in strings)
        {
            fileWriter.Write(Encoding.UTF8.GetBytes(s));
            fileWriter.Write((byte)0);
        }
        return file.ToArray();
    }
}
