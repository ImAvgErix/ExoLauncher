using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// A store manifest rewritten with the same bytes (the Riot Client's RiotClientInstalls.json, every half minute) must not
/// rescan the library. Ways this fails: a real change taken for a rewrite, so a new install never shows; a file first
/// seen or unreadable taken as unchanged; the remembered bytes not updated, so every later write looks new or old.
/// </summary>
public sealed class LibraryWatchersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-watch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Only_a_rewrite_with_the_same_bytes_counts_as_unchanged()
    {
        using var watchers = new LibraryWatchers(() => []);
        var file = Path.Combine(_dir, "RiotClientInstalls.json");

        File.WriteAllText(file, """{"rc_default":"C:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.False(watchers.SameAsBefore(file));
        File.WriteAllText(file, """{"rc_default":"C:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.True(watchers.SameAsBefore(file));
        File.WriteAllText(file, """{"rc_default":"D:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.False(watchers.SameAsBefore(file));
        Assert.True(watchers.SameAsBefore(file));

        Assert.False(watchers.SameAsBefore(Path.Combine(_dir, "missing.json")));
    }
}
