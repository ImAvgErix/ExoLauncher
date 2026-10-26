using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Which folder a cancelled Epic install is allowed to name for deletion. The base folder is shared with other installs
/// and other programs, so the ways it could go wrong are: a stranger's new folder is taken for the game's and deleted
/// with everything in it; two new folders and a guess between them; a folder that was there before the install; the base
/// folder missing; the cancel arriving before the base was even read.
/// </summary>
public sealed class EpicPartialInstallTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "exo-epic-" + Guid.NewGuid().ToString("N"));

    public EpicPartialInstallTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private string Make(string name, bool legendary)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(legendary ? Path.Combine(dir, ".egstore") : dir);
        return dir;
    }

    private HashSet<string> Snapshot() => Directory.GetDirectories(_base).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_new_folder_with_legendarys_marker_is_the_partial_install()
    {
        var before = Snapshot();
        var game = Make("Fortnite", legendary: true);
        Assert.Equal(game, EpicAdapter.PartialInstallFolder(_base, before));
    }

    [Fact]
    public void A_new_folder_without_the_marker_is_never_named()
    {
        var before = Snapshot();
        Make("SomeoneElsesProject", legendary: false);
        Assert.Null(EpicAdapter.PartialInstallFolder(_base, before));
    }

    [Fact]
    public void A_strangers_folder_beside_the_games_is_not_taken_for_it()
    {
        var before = Snapshot();
        var game = Make("Game", legendary: true);
        Make("Documents-backup", legendary: false);
        Assert.Equal(game, EpicAdapter.PartialInstallFolder(_base, before));
    }

    [Fact]
    public void Two_candidates_are_a_guess_so_nothing_is_named()
    {
        var before = Snapshot();
        Make("A", legendary: true);
        Make("B", legendary: true);
        Assert.Null(EpicAdapter.PartialInstallFolder(_base, before));
    }

    [Fact]
    public void A_folder_that_was_there_before_is_never_named()
    {
        Make("Old", legendary: true);
        Assert.Null(EpicAdapter.PartialInstallFolder(_base, Snapshot()));
    }

    [Fact]
    public void A_cancel_before_the_base_was_read_or_a_missing_base_names_nothing()
    {
        Make("Game", legendary: true);
        Assert.Null(EpicAdapter.PartialInstallFolder(_base, null));
        Assert.Null(EpicAdapter.PartialInstallFolder(Path.Combine(_base, "missing"), new HashSet<string>()));
    }
}
