using System.Diagnostics;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Adapters.Riot;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 5 — hostile local files. Giant or malformed settings.json,
/// appinfo.vdf, appmanifest ACF, localconfig.vdf and Riot YAML are fed to the
/// real parsers. Each must return a bounded result quickly without throwing,
/// hanging, or allocating without limit.
/// </summary>
public class HostileFileAttacks
{
    // A generous budget: every parse here should finish far inside this even on
    // multi-megabyte input. A blown budget signals a hang or quadratic blowup.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static void WithinBudget(Action body)
    {
        var sw = Stopwatch.StartNew();
        var t = Task.Run(body);
        Assert.True(t.Wait(Budget), $"parser did not finish within {Budget.TotalSeconds}s (hang / unbounded work)");
        // surface any exception
        t.GetAwaiter().GetResult();
        Assert.True(sw.Elapsed < Budget);
    }

    // ---- appinfo.vdf (binary) ----

    [Fact]
    public void SteamAppInfo_rejects_bad_magic_and_truncation()
    {
        WithinBudget(() =>
        {
            Assert.Empty(SteamAppInfoNames.Parse([]));
            Assert.Empty(SteamAppInfoNames.Parse(new byte[8]));
            Assert.Empty(SteamAppInfoNames.Parse(Encoding.ASCII.GetBytes("not a vdf at all, just text")));
            // Correct magic, then garbage table offset.
            var b = new byte[64];
            BitConverter.GetBytes(0x07564429u).CopyTo(b, 0);
            BitConverter.GetBytes((long)int.MaxValue).CopyTo(b, 8); // offset past end
            Assert.Empty(SteamAppInfoNames.Parse(b));
        });
    }

    [Fact]
    public void SteamAppInfo_survives_large_random_blob()
    {
        WithinBudget(() =>
        {
            var rnd = new Random(1);
            var b = new byte[32 * 1024 * 1024];
            rnd.NextBytes(b);
            BitConverter.GetBytes(0x07564429u).CopyTo(b, 0);
            // random table offset within range, string count may be huge — the
            // parser caps count at 1,000,000 and bounds every read.
            BitConverter.GetBytes((long)(b.Length / 2)).CopyTo(b, 8);
            var result = SteamAppInfoNames.Parse(b);
            Assert.NotNull(result); // whatever it is, it returned bounded.
        });
    }

    [Fact]
    public void SteamAppInfo_rejects_absurd_string_table_count()
    {
        WithinBudget(() =>
        {
            var b = new byte[128];
            BitConverter.GetBytes(0x07564429u).CopyTo(b, 0);
            BitConverter.GetBytes((long)16).CopyTo(b, 8);   // table right after header
            BitConverter.GetBytes(int.MaxValue).CopyTo(b, 16); // count = 2.1 billion
            Assert.Empty(SteamAppInfoNames.Parse(b)); // count>1,000,000 -> empty table -> empty result
        });
    }

    // ---- appmanifest ACF (text/VDF) ----

    [Fact]
    public void Acf_parser_handles_malformed_and_giant()
    {
        WithinBudget(() =>
        {
            Assert.NotNull(SteamProtocol.ParseAcfFields(""));
            Assert.NotNull(SteamProtocol.ParseAcfFields("\"unterminated"));
            Assert.NotNull(SteamProtocol.ParseAcfFields(new string('"', 100_000)));
            // deeply nested braces
            Assert.NotNull(SteamProtocol.ParseAcfFields(new string('{', 500_000) + new string('}', 500_000)));
            // many pairs
            var sb = new StringBuilder();
            for (var i = 0; i < 200_000; i++) sb.Append($"\"k{i}\" \"v{i}\"\n");
            var fields = SteamProtocol.ParseAcfFields(sb.ToString());
            Assert.True(fields.Count > 0);
            // backslash-escape storms do not loop forever
            Assert.NotNull(SteamProtocol.ParseAcfFields("\"a\" \"" + new string('\\', 100_000) + "\""));
        });
    }

    [Fact]
    public void Acf_TryParseAppManifest_rejects_invalid_appid()
    {
        WithinBudget(() =>
        {
            var fields = SteamProtocol.ParseAcfFields("\"appid\" \"notanumber\"\n\"name\" \"X\"");
            Assert.False(SteamProtocol.TryParseAppManifest(fields, out _, out _, out _, out _));

            var ok = SteamProtocol.ParseAcfFields("\"appid\" \"440\"\n\"name\" \"Team Fortress 2\"\n\"installdir\" \"tf\"");
            Assert.True(SteamProtocol.TryParseAppManifest(ok, out var id, out var name, out _, out _));
            Assert.Equal("440", id);
            Assert.Equal("Team Fortress 2", name);
        });
    }

    // ---- localconfig.vdf is parsed with the same ACF field reader ----

    [Fact]
    public void LocalConfig_shaped_text_is_bounded()
    {
        WithinBudget(() =>
        {
            var sb = new StringBuilder("\"UserLocalConfigStore\"\n{\n");
            for (var i = 0; i < 100_000; i++) sb.Append($"  \"{i}\" {{ \"LastPlayed\" \"{i}\" }}\n");
            sb.Append('}');
            Assert.NotNull(SteamProtocol.ParseAcfFields(sb.ToString()));
        });
    }

    // ---- Riot product_settings.yaml ----

    [Fact]
    public void Riot_yaml_parser_is_bounded_and_safe()
    {
        WithinBudget(() =>
        {
            Assert.NotNull(RiotProductMetadata.ParseSettings(""));
            Assert.NotNull(RiotProductMetadata.ParseSettings(": : : :"));
            Assert.NotNull(RiotProductMetadata.ParseSettings(new string('\n', 1_000_000)));
            // a huge single line with no colon
            Assert.NotNull(RiotProductMetadata.ParseSettings(new string('a', 5_000_000)));
            // path traversal in the install path is normalized, not blindly trusted
            var s = RiotProductMetadata.ParseSettings("product_install_full_path: \"..\\\\..\\\\Windows\"");
            Assert.NotNull(s);
            // relative paths are rejected (NormalizePath returns null for non-rooted)
            var rel = RiotProductMetadata.ParseSettings("product_install_full_path: \"relative\\\\path\"");
            Assert.Null(rel.InstallFullPath);
        });
    }

    // ---- upscaler catalog manifest (DLSS Swapper JSON) ----

    [Fact]
    public void Upscaler_manifest_parser_is_bounded_and_rejects_offhost_urls()
    {
        WithinBudget(() =>
        {
            // Was a finding: malformed JSON threw out of ParseManifest. It now returns
            // empty, which every caller already treats as "keep the pinned releases".
            Assert.Empty(DlssSwapService.ParseManifest(Encoding.UTF8.GetBytes("not json")));
            Assert.Empty(DlssSwapService.ParseManifest(Encoding.UTF8.GetBytes("{\"dlss\": [")));
            Assert.Empty(DlssSwapService.ParseManifest(Encoding.UTF8.GetBytes("[]")));
            Assert.Empty(DlssSwapService.ParseManifest(Encoding.UTF8.GetBytes("{}")));
            // A manifest whose download_url points off the archive host must be
            // dropped (ParseManifest requires the dlss-swapper-downloads host).
            var evil = """
            {"dlss":[{"version":"3.5.0.0","download_url":"https://evil.com/dlss/x.zip","md5_hash":"0123456789abcdef0123456789abcdef","file_size":10,"zip_file_size":5,"is_signature_valid":true}]}
            """;
            Assert.Empty(DlssSwapService.ParseManifest(Encoding.UTF8.GetBytes(evil)));
        });
    }

    // ---- settings.json through the real SettingsService ----

    [Fact]
    public void Settings_load_survives_corrupt_and_giant_files()
    {
        var prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var dir = Path.Combine(Path.GetTempPath(), "exo-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, dir);
            var path = Path.Combine(dir, "settings.json");

            foreach (var payload in new[]
                     {
                         "{ this is not valid json",
                         "[]",
                         "null",
                         "{\"onboardingComplete\": \"notabool\"}",
                         "{" + string.Concat(Enumerable.Repeat("\"a\":1,", 50_000)) + "\"b\":2}",
                     })
            {
                File.WriteAllText(path, payload);
                WithinBudget(() =>
                {
                    var svc = new SettingsService();
                    svc.Load();
                    Assert.NotNull(svc.Current); // never throws; falls back to defaults
                });
            }

            // Pins are retired; a file from before still loads and keeps the rest.
            File.WriteAllText(path, "{\"favorites\":[\"steam:1\"],\"sortMode\":\"played\"}");
            WithinBudget(() =>
            {
                var svc = new SettingsService();
                svc.Load();
                Assert.Equal("played", svc.Current.SortMode);
            });

            // A giant but structurally valid file with deep nesting.
            File.WriteAllText(path, "{\"Favorites\":[" + string.Join(",", Enumerable.Range(0, 200_000).Select(i => $"\"g{i}\"")) + "]}");
            WithinBudget(() =>
            {
                var svc = new SettingsService();
                svc.Load();
                Assert.NotNull(svc.Current);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, prior);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
