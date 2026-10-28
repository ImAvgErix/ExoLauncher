using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo Notes could lose or damage words without saying so:
/// - a note's first line names a file Windows refuses (a reserved name, a refused character, a trailing dot) or no name;
/// - a note following its first line takes another note's file, or one you renamed is renamed again by its first line;
/// - a file is written back in another encoding (ANSI, UTF-16, a UTF-8 byte-order mark) or with other line endings;
/// - a file opened from elsewhere is written as you type, not only on Save;
/// - a file from elsewhere's unsaved edits, or the open tabs, don't come back after a restart;
/// - closing a file from elsewhere keeps its edits around, or a tab points at a note that is gone;
/// - a binary file opens as text;
/// - a title keeps Markdown's marks.
/// </summary>
public sealed class NotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-notes-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "Exo Notes");
    private string StatePath => Path.Combine(_root, "state", "notes.json");

    public NotesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private NoteStore Store() => new(StatePath, Folder);

    [Theory]
    [InlineData("Shopping list\nmilk", "Shopping list")]
    [InlineData("# Raid plan: week 2?\n", "Raid plan week 2")]
    [InlineData("- [ ] buy keys", "buy keys")]
    [InlineData("con", "con note")]
    [InlineData("COM1", "COM1 note")]
    [InlineData("ends with dots...", "ends with dots")]
    [InlineData("a/b\\c|d", "a b c d")]
    public void AFirstLineNamesAFileWindowsTakes(string text, string name) => Assert.Equal(name, NoteStore.FileName(text));

    [Fact]
    public void NoWordsStillGetAName() => Assert.StartsWith("Note ", NoteStore.FileName("\n  \n"));

    [Fact]
    public void ATitleLosesItsMarks()
    {
        var (title, snippet) = NoteStore.TitleAndSnippet("\n## **Boss** order\n\n- [x] first\n- second");
        Assert.Equal("Boss order", title);
        Assert.Equal("first second", snippet);
    }

    [Fact]
    public void ANoteFollowsItsFirstLineWithoutTakingAnothersFile()
    {
        var store = Store();
        var other = store.Create("Plans", markdown: false, gameId: null, out _)!;
        var note = store.Create("Pla", markdown: false, gameId: "steam:1", out _)!;
        Assert.Equal("Pla.txt", note.Name);
        var renamed = store.Write(note.Id, "Plans\nmore", toFile: false, out var problem)!;
        Assert.Null(problem);
        Assert.Equal("Plans 2.txt", renamed.Name);
        Assert.Equal("Plans", File.ReadAllText(Path.Combine(Folder, other.Name)));
        Assert.Equal("Plans\r\nmore", File.ReadAllText(Path.Combine(Folder, "Plans 2.txt")));
        // Already "Plans 2" beside "Plans": stays.
        Assert.Equal("Plans 2.txt", store.Write(note.Id, "Plans\nmore words", toFile: false, out _)!.Name);
        Assert.Equal("steam:1", renamed.GameId);
    }

    [Fact]
    public void ANoteYouRenamedKeepsYourName()
    {
        var store = Store();
        var note = store.Create("Draft", markdown: true, gameId: null, out _)!;
        Assert.Equal("Draft.md", note.Name);
        Assert.Equal("Mine.md", store.Rename(note.Id, "Mine.md", out _)!.Name);
        Assert.Equal("Mine.md", store.Write(note.Id, "Something else", toFile: false, out _)!.Name);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A, (byte)'b' }, "utf-8-bom", "crlf")]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0, 0x0A, 0, (byte)'b', 0 }, "utf-16le", "lf")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a', 0, 0x0D, 0, (byte)'b' }, "utf-16be", "cr")]
    [InlineData(new byte[] { (byte)'c', 0xE9, 0x0A, (byte)'d' }, "ansi", "lf")]
    [InlineData(new byte[] { (byte)'c', 0xC3, 0xA9, 0x0D, 0x0A, (byte)'d' }, "utf-8", "crlf")]
    public void AFileIsWrittenBackAsItWasFound(byte[] bytes, string encoding, string eol)
    {
        var path = Path.Combine(_root, "found.txt");
        File.WriteAllBytes(path, bytes);
        var store = Store();
        var id = store.Open(path, out var problem)!;
        Assert.Null(problem);
        var read = store.Read(id, out _)!;
        Assert.Equal(encoding, read.Info.Encoding);
        Assert.Equal(eol, read.Info.Eol);
        Assert.DoesNotContain('\r', read.Text);
        Assert.NotNull(store.Write(id, read.Text, toFile: true, out _));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void AFileFromElsewhereIsWrittenOnlyBySaveAndItsEditsComeBack()
    {
        var path = Path.Combine(_root, "config.txt");
        File.WriteAllText(path, "kept");
        var store = Store();
        var id = store.Open(path, out _)!;
        var edited = store.Write(id, "typed", toFile: false, out _)!;
        Assert.True(edited.External);
        Assert.True(edited.Unsaved);
        Assert.Equal("kept", File.ReadAllText(path));

        var again = Store();
        var (_, tabs, active) = again.List();
        Assert.Equal([id], tabs);
        Assert.Equal(id, active);
        var restored = again.Read(id, out _)!;
        Assert.Equal("typed", restored.Text);
        Assert.True(restored.Info.Unsaved);

        Assert.False(again.Write(id, "typed", toFile: true, out _)!.Unsaved);
        Assert.Equal("typed", File.ReadAllText(path));
    }

    [Fact]
    public void ClosingAFileFromElsewhereForgetsItButNotANote()
    {
        var path = Path.Combine(_root, "elsewhere.txt");
        File.WriteAllText(path, "x");
        var store = Store();
        var file = store.Open(path, out _)!;
        store.Write(file, "unsaved", toFile: false, out _);
        var note = store.Create("Mine", markdown: false, gameId: null, out _)!;
        store.Session([], null);
        var (notes, tabs, _) = Store().List();
        Assert.Empty(tabs);
        Assert.DoesNotContain(notes, n => n.Id == file);
        Assert.Contains(notes, n => n.Id == note.Id && !n.External);
    }

    [Fact]
    public void AFileDroppedInTheFolderJoinsAndAGoneOneLeavesItsTab()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "todo.md"), "# Todo\n- [ ] one");
        var store = Store();
        var (notes, _, _) = store.List();
        var todo = Assert.Single(notes);
        Assert.Equal("Todo", todo.Title);
        Assert.Equal("markdown", todo.Kind);
        store.Session([todo.Id], todo.Id);
        File.Delete(Path.Combine(Folder, "todo.md"));
        var (after, tabs, active) = store.List();
        Assert.Empty(after);
        Assert.Empty(tabs);
        Assert.Null(active);
        // Its name was yours: a first line never renames it.
        File.WriteAllText(Path.Combine(Folder, "todo.md"), "x");
        var back = Assert.Single(store.List().Notes);
        Assert.Equal("todo.md", store.Write(back.Id, "Other title", toFile: false, out _)!.Name);
    }

    [Fact]
    public void SearchReadsEveryNotesWords()
    {
        var store = Store();
        store.Create("Groceries\nmilk and eggs", markdown: false, gameId: null, out _);
        store.Create("Raid\nboss at midnight", markdown: false, gameId: null, out _);
        Assert.Equal("Raid", Assert.Single(store.List("MIDNIGHT").Notes).Title);
        Assert.Equal(2, store.List(" ").Notes.Count);
    }

    [Fact]
    public void ABinaryFileDoesNotOpen()
    {
        var path = Path.Combine(_root, "game.dat");
        File.WriteAllBytes(path, [1, 2, 0, 4]);
        Assert.Null(Store().Open(path, out var problem));
        Assert.Equal("This isn't a text file.", problem);
    }

    [Fact]
    public void MarkdownOrTextRenamesOnlyExosOwnNotes()
    {
        var store = Store();
        var note = store.Create("Quests", markdown: false, gameId: null, out _)!;
        Assert.Equal("Quests.md", store.SetKind(note.Id, markdown: true, out _)!.Name);
        var path = Path.Combine(_root, "readme.txt");
        File.WriteAllText(path, "x");
        var file = store.Open(path, out _)!;
        Assert.Null(store.SetKind(file, markdown: true, out var problem));
        Assert.NotNull(problem);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SpellingIsOffForLogsAndCode()
    {
        var store = Store();
        foreach (var (name, on) in new[] { ("a.log", false), ("b.json", false), ("c.txt", true) })
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "x", Encoding.UTF8);
            Assert.Equal(on, store.Read(store.Open(path, out _)!, out _)!.Info.Spelling);
        }
    }

    [Fact]
    public void SaveAsMovesAFileFromElsewhereAndCopiesANote()
    {
        var store = Store();
        var from = Path.Combine(_root, "old.txt");
        File.WriteAllText(from, "old");
        var file = store.Open(from, out _)!;
        var moved = store.SaveAs(file, Path.Combine(_root, "new.txt"), "edited", out _)!;
        Assert.Equal("old", File.ReadAllText(from));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(_root, "new.txt")));
        Assert.Equal([moved], store.List().Tabs);

        var note = store.Create("Keep me", markdown: false, gameId: null, out _)!;
        var copy = store.SaveAs(note.Id, Path.Combine(_root, "copy.txt"), "Keep me", out _)!;
        Assert.Equal([moved, note.Id, copy], store.List().Tabs);
        Assert.True(File.Exists(Path.Combine(Folder, "Keep me.txt")));
    }

    [Fact]
    public void ANoteArgumentIsThePathAfterNote()
    {
        Assert.Equal(@"C:\a b\c.txt", NoteTextFiles.NoteArgument(NoteTextFiles.Split(@"""C:\Exo\ExoLauncher.exe"" --note ""C:\a b\c.txt""")));
        Assert.Null(NoteTextFiles.NoteArgument(NoteTextFiles.Split(@"""C:\Exo\ExoLauncher.exe"" --note")));
        Assert.Null(NoteTextFiles.NoteArgument(["C:\\file.txt"]));
    }
}
