using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// web-client-preferences.json read through the real WebClientPreferences.
/// Ways it could fail: a music service saved before Cider was removed reads
/// back as "cider", which no room hosts any more, so the Music room and the
/// startup prewarm silently do nothing; the move rewrites other services or
/// drops the permission choices beside it.
/// </summary>
public sealed class WebClientPreferencesTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-webprefs-" + Guid.NewGuid().ToString("N"));

    public WebClientPreferencesTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Write(string json) =>
        File.WriteAllText(Path.Combine(PathHelper.AppDataDir, "web-client-preferences.json"), json);

    [Fact]
    public void A_saved_Cider_choice_reads_as_Apple_Music()
    {
        Write("""{"musicService":"cider","microphone":"allow"}""");
        var options = WebClientPreferences.Reload();
        Assert.Equal("apple", options.MusicService);
        Assert.Equal("allow", options.Microphone);
        Assert.False(MusicWebPolicy.IsService("cider"));
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("apple")]
    [InlineData("youtube")]
    public void Other_services_read_back_unchanged(string service)
    {
        Write("{\"musicService\":\"" + service + "\"}");
        Assert.Equal(service, WebClientPreferences.Reload().MusicService);
    }
}
