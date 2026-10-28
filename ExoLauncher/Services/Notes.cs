using System.Globalization;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// One note as the page sees it: never its path. <paramref name="Name"/> is the file's name; <paramref name="External"/> a
/// file opened from elsewhere (written only by Save); <paramref name="Unsaved"/> its edits not written yet.
/// </summary>
internal sealed record NoteInfo(string Id, string Title, string Snippet, string Kind, string Name, DateTime Edited, long Size,
    bool Pinned, bool External, bool Unsaved, string? GameId, string Encoding, string Eol, bool Spelling);

/// <summary>A note's words (lines end in \n) and what the page shows of it.</summary>
internal sealed record NoteText(string Text, NoteInfo Info);

internal sealed class NoteEntry
{
    public string Path { get; set; } = "";
    /// <summary>Named by you (or a file that was there): its name no longer follows its first line.</summary>
    public bool Named { get; set; } = true;
    public bool Pinned { get; set; }
    /// <summary>The game running when it was written.</summary>
    public string? GameId { get; set; }
    /// <summary>A file opened from elsewhere: its edits until Save, kept here so they outlive a restart.</summary>
    public string? Draft { get; set; }
    public DateTime? DraftAt { get; set; }
    /// <summary>utf-8, utf-8-bom, utf-16le, utf-16be or ansi, as the file was found; written back the same way.</summary>
    public string Encoding { get; set; } = "utf-8";
    /// <summary>crlf, lf or cr.</summary>
    public string Eol { get; set; } = "crlf";
}

internal sealed class NotesState
{
    /// <summary>Where notes go, kept once chosen, so moving Documents later doesn't lose them.</summary>
    public string? Folder { get; set; }
    public Dictionary<string, NoteEntry> Notes { get; set; } = new(StringComparer.Ordinal);
    public List<string> Tabs { get; set; } = new();
    public string? Active { get; set; }
}

/// <summary>
/// Exo Notes' files. Every note is a plain .txt or .md file in the Notes folder (<see cref="DefaultFolder"/>), written as
/// you type; a note Exo named takes its first line as its file name as that changes, one you renamed keeps yours. A file
/// opened from elsewhere (Open, or Windows opening a text file with Exo) is edited where it is and written only by Save,
/// its edits kept until then. notes.json, beside Exo's other data, holds each note's id, where it is, its pin and game,
/// the open tabs and those edits, so tabs and unsaved words come back after a restart. Nothing leaves this PC.
/// </summary>
internal sealed class NoteStore
{
    /// <summary>Bigger files are not text anyone edits by hand (and would stall the page).</summary>
    internal const int MaxChars = 8_000_000;
    private const int MaxFiles = 2000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly string[] NoteExtensions = [".txt", ".md"];
    // Files Windows' spell check would only underline: logs, settings and code.
    private static readonly string[] NoSpelling = [".log", ".ini", ".cfg", ".conf", ".json", ".xml", ".yaml", ".yml", ".toml", ".csv",
        ".reg", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".ts", ".tsx", ".jsx", ".cs", ".cpp", ".c", ".h", ".hpp", ".java", ".lua",
        ".css", ".html", ".htm", ".sql", ".vdf", ".properties", ".env"];

    private readonly string _statePath;
    // A Monitor: Folder and Own are read inside the other locked calls.
    private readonly object _gate = new();
    private NotesState? _state;

    internal NoteStore(string statePath, string? folder = null)
    {
        _statePath = statePath;
        if (folder is not null) State.Folder = folder;
    }

    internal static NoteStore Shared { get; } = new(Path.Combine(PathHelper.AppDataDir, "notes", "notes.json"));

    /// <summary>
    /// Documents\Exo Notes; the user folder's Exo Notes when Documents is kept in OneDrive, since a note there would be
    /// carried to the cloud.
    /// </summary>
    internal static string DefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var synced = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Any(root => !string.IsNullOrWhiteSpace(root) && Under(documents, root!));
        return Path.Combine(string.IsNullOrEmpty(documents) || synced ? profile : documents, "Exo Notes");
    }

    internal string Folder
    {
        get
        {
            lock (_gate)
            {
                if (State.Folder is null) { State.Folder = DefaultFolder(); TrySave(); }
                return State.Folder;
            }
        }
    }

    private NotesState State
    {
        get
        {
            if (_state is not null) return _state;
            try
            {
                if (File.Exists(_statePath))
                    _state = JsonSerializer.Deserialize<NotesState>(File.ReadAllText(_statePath), Json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn("Notes: notes.json unreadable, starting over: " + ex.GetType().Name);
            }
            _state ??= new NotesState();
            _state.Notes = new Dictionary<string, NoteEntry>(_state.Notes ?? new(), StringComparer.Ordinal);
            _state.Tabs ??= new();
            return _state;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State, Json));
        File.Move(temp, _statePath, overwrite: true);
    }

    private static bool Under(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One of Exo's own notes: a .txt or .md straight in the Notes folder.</summary>
    private bool Own(NoteEntry entry) =>
        string.Equals(Path.GetDirectoryName(entry.Path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder)), StringComparison.OrdinalIgnoreCase)
        && NoteExtensions.Contains(Path.GetExtension(entry.Path), StringComparer.OrdinalIgnoreCase);

    private static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Every note in the folder (pinned first, then the latest), the files opened from elsewhere still open, the tabs and
    /// the one in front. A file put in the folder by hand joins as a note named by you; a note whose file went is dropped
    /// unless it has edits still to save. <paramref name="query"/>: only notes whose words hold it, in any case.
    /// </summary>
    internal (IReadOnlyList<NoteInfo> Notes, IReadOnlyList<string> Tabs, string? Active) List(string? query = null)
    {
        lock (_gate)
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            var byPath = State.Notes.ToDictionary(pair => pair.Value.Path, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var file in Directory.EnumerateFiles(folder).Where(file => NoteExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Take(MaxFiles))
            {
                if (byPath.ContainsKey(file)) continue;
                var id = NewId();
                State.Notes[id] = new NoteEntry { Path = file, Named = true };
                byPath[file] = id;
                changed = true;
            }
            foreach (var (id, entry) in State.Notes.ToList())
            {
                if (File.Exists(entry.Path) || entry.Draft is not null) continue;
                State.Notes.Remove(id);
                changed = true;
            }
            changed |= TidyTabs();
            if (changed) TrySave();
            var needle = query?.Trim();
            var notes = State.Notes.Where(pair => string.IsNullOrEmpty(needle) || Holds(pair.Value, needle))
                .Select(pair => Info(pair.Key, pair.Value, Peek(pair.Value)))
                .OrderByDescending(note => note.Pinned).ThenByDescending(note => note.Edited).ToList();
            return (notes, State.Tabs.ToList(), State.Active);
        }
    }

    private bool TidyTabs()
    {
        var before = State.Tabs.Count;
        State.Tabs = State.Tabs.Where(State.Notes.ContainsKey).Distinct().ToList();
        var changed = before != State.Tabs.Count;
        if (State.Active is { } active && !State.Tabs.Contains(active)) { State.Active = State.Tabs.FirstOrDefault(); changed = true; }
        return changed;
    }

    private static bool Holds(NoteEntry entry, string needle)
    {
        if (entry.Draft is { } draft) return draft.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
        if (Path.GetFileName(entry.Path).Contains(needle, StringComparison.CurrentCultureIgnoreCase)) return true;
        var (text, _) = ReadFile(entry.Path, out _);
        return text?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    /// <summary>The start of a note, for its title and snippet: its draft, else the file's first 4 KB.</summary>
    private static string Peek(NoteEntry entry)
    {
        if (entry.Draft is { } draft) return draft.Length > 4096 ? draft[..4096] : draft;
        try
        {
            using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Decode(buffer.AsSpan(0, read).ToArray(), partial: true).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    internal NoteText? Read(string id, out string? problem)
    {
        problem = null;
        lock (_gate)
        {
            if (!State.Notes.TryGetValue(id, out var entry)) { problem = "That note is gone."; return null; }
            if (entry.Draft is { } draft) return new NoteText(draft, Info(id, entry, draft));
            var (text, format) = ReadFile(entry.Path, out problem);
            if (text is null) return null;
            if (entry.Encoding != format.Encoding || entry.Eol != format.Eol)
            {
                entry.Encoding = format.Encoding;
                entry.Eol = format.Eol;
                TrySave();
            }
            return new NoteText(text, Info(id, entry, text));
        }
    }

    private static (string? Text, TextFormat Format) ReadFile(string path, out string? problem)
    {
        problem = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { problem = "That file is gone."; return (null, default); }
            if (info.Length > MaxChars * 2L) { problem = "This file is too big for Notes."; return (null, default); }
            var bytes = File.ReadAllBytes(path);
            var decoded = Decode(bytes);
            if (decoded.Binary) { problem = "This isn't a text file."; return (null, default); }
            if (decoded.Text.Length > MaxChars) { problem = "This file is too big for Notes."; return (null, default); }
            return (decoded.Text, decoded.Format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Notes: read failed: " + ex.GetType().Name);
            problem = ex is UnauthorizedAccessException ? "Windows won't let Exo read this file." : "Exo could not read this file. It may be open in another app.";
            return (null, default);
        }
    }

    private NoteInfo Info(string id, NoteEntry entry, string text)
    {
        var (title, snippet) = TitleAndSnippet(text);
        var file = new FileInfo(entry.Path);
        var exists = file.Exists;
        var extension = Path.GetExtension(entry.Path);
        var own = Own(entry);
        return new NoteInfo(id,
            title.Length > 0 ? title : own ? "New note" : Path.GetFileName(entry.Path),
            snippet,
            IsMarkdown(entry.Path) ? "markdown" : "text",
            Path.GetFileName(entry.Path),
            entry.DraftAt is { } at && (!exists || at > file.LastWriteTimeUtc) ? at : exists ? file.LastWriteTimeUtc : DateTime.UtcNow,
            exists ? file.Length : 0,
            entry.Pinned,
            !own,
            entry.Draft is not null,
            entry.GameId,
            entry.Encoding,
            entry.Eol,
            !NoSpelling.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>The first line with words, without its Markdown marks; then the next words, on one line.</summary>
    internal static (string Title, string Snippet) TitleAndSnippet(string text)
    {
        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, line => Plain(line).Length > 0);
        if (first < 0) return ("", "");
        var title = Plain(lines[first]);
        if (title.Length > 80) title = title[..80].TrimEnd();
        var rest = new StringBuilder();
        foreach (var line in lines.Skip(first + 1))
        {
            var words = Plain(line);
            if (words.Length == 0) continue;
            if (rest.Length > 0) rest.Append(' ');
            rest.Append(words);
            if (rest.Length >= 140) break;
        }
        var snippet = rest.ToString();
        return (title, snippet.Length > 140 ? snippet[..140].TrimEnd() : snippet);
    }

    /// <summary>A line's words: headings, quotes, bullets, checkboxes and emphasis marks taken off.</summary>
    private static string Plain(string line)
    {
        var s = line.Trim().TrimStart('#', '>').Trim();
        if (s.StartsWith("- [ ] ", StringComparison.Ordinal) || s.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        else if (s.Length > 1 && s[0] is '-' or '*' or '+' && s[1] == ' ') s = s[2..];
        return s.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("~~", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal).Trim();
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// A new note, in the Notes folder, named for its first line: Markdown (.md) or text (.txt). Empty words make no file.
    /// </summary>
    internal NoteInfo? Create(string text, bool markdown, string? gameId, out string? problem)
    {
        problem = null;
        lock (_gate)
        {
            var folder = Folder;
            try
            {
                Directory.CreateDirectory(folder);
                var extension = markdown ? ".md" : ".txt";
                var path = Unique(folder, FileName(text), extension, null);
                var entry = new NoteEntry { Path = path, Named = false, GameId = gameId, Encoding = "utf-8", Eol = "crlf" };
                WriteFile(entry, text, fresh: true);
                var id = NewId();
                State.Notes[id] = entry;
                if (!State.Tabs.Contains(id)) State.Tabs.Add(id);
                State.Active = id;
                Save();
                return Info(id, entry, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Notes: create failed: " + ex.GetType().Name);
                problem = "Exo could not save the note.";
                return null;
            }
        }
    }

    /// <summary>
    /// Writes a note's words. One of Exo's notes is written at once (and follows its first line's name while Exo named
    /// it); a file from elsewhere keeps them as unsaved edits unless <paramref name="toFile"/> (Save).
    /// </summary>
    internal NoteInfo? Write(string id, string text, bool toFile, out string? problem)
    {
        problem = null;
        if (text.Length > MaxChars) { problem = "This note is too big to save."; return null; }
        lock (_gate)
        {
            if (!State.Notes.TryGetValue(id, out var entry)) { problem = "That note is gone."; return null; }
            var own = Own(entry);
            if (!own && !toFile)
            {
                entry.Draft = text;
                entry.DraftAt = DateTime.UtcNow;
                TrySave();
                return Info(id, entry, text);
            }
            try
            {
                WriteFile(entry, text, fresh: false);
                entry.Draft = null;
                entry.DraftAt = null;
                if (own && !entry.Named) FollowFirstLine(entry, text);
                TrySave();
                return Info(id, entry, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Notes: write failed: " + ex.GetType().Name);
                // Kept, so nothing typed is lost; Save says so.
                entry.Draft = text;
                entry.DraftAt = DateTime.UtcNow;
                TrySave();
                problem = new FileInfo(entry.Path) is { Exists: true, IsReadOnly: true } ? "This file is read-only. Your words are kept here."
                    : ex is UnauthorizedAccessException ? "Windows won't let Exo write this file. Your words are kept here."
                    : "Exo could not write the file. It may be open in another app. Your words are kept here.";
                return null;
            }
        }
    }

    /// <summary>Renames a note Exo named when its first line names it differently; a file in use keeps its name.</summary>
    private static void FollowFirstLine(NoteEntry entry, string text)
    {
        if (TitleAndSnippet(text).Title.Length == 0) return;
        var wanted = FileName(text);
        var current = Path.GetFileNameWithoutExtension(entry.Path);
        // "Shopping list 2" already is "Shopping list" beside another.
        if (wanted == current || StripCounter(current) == wanted) return;
        var target = Unique(Path.GetDirectoryName(entry.Path)!, wanted, Path.GetExtension(entry.Path), entry.Path);
        if (string.Equals(target, entry.Path, StringComparison.Ordinal)) return;
        try
        {
            File.Move(entry.Path, target);
            entry.Path = target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Notes: rename skipped: " + ex.GetType().Name); }
    }

    /// <summary>"Shopping list 2" → "Shopping list".</summary>
    private static string StripCounter(string name)
    {
        var space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? name[..space] : name;
    }

    /// <summary>The file name a note's first line gives: no characters Windows refuses, no reserved name, at most 60.</summary>
    internal static string FileName(string text)
    {
        var (title, _) = TitleAndSnippet(text);
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 60) name = name[..60];
        name = name.TrimEnd(' ', '.');
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]))
            name += " note";
        return name.Length > 0 ? name : "Note " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
    }

    /// <summary>name.ext in folder, or name 2.ext and up when that is another file's; <paramref name="self"/> is never another's.</summary>
    private static string Unique(string folder, string name, string extension, string? self)
    {
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, (n == 1 ? name : $"{name} {n}") + extension);
            if (self is not null && string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) return self;
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>
    /// The words written as the file was found (its encoding and line endings), through a file beside it that then takes
    /// its place, so a failed write never leaves half a file. A file of yours keeps its attributes and permissions.
    /// </summary>
    private static void WriteFile(NoteEntry entry, string text, bool fresh)
    {
        var eol = entry.Eol switch { "lf" => "\n", "cr" => "\r", _ => "\r\n" };
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (eol != "\n") body = body.Replace("\n", eol, StringComparison.Ordinal);
        var bytes = Encode(body, entry.Encoding);
        if (fresh || !File.Exists(entry.Path))
        {
            using (var stream = new FileStream(entry.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
            return;
        }
        if (new FileInfo(entry.Path).IsReadOnly) throw new UnauthorizedAccessException();
        var temp = Path.Combine(Path.GetDirectoryName(entry.Path)!, "." + Path.GetFileName(entry.Path) + "." + NewId() + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            // A drive that can't swap files (some network and FAT drives): written over in place.
            try { File.Replace(temp, entry.Path, null, ignoreMetadataErrors: true); }
            catch (IOException) when (File.Exists(temp)) { File.Copy(temp, entry.Path, overwrite: true); }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ encoding

    internal readonly record struct TextFormat(string Encoding, string Eol);

    /// <summary>
    /// A file's words with \n line ends, and how it was written: a byte-order mark says UTF-8 or UTF-16; else UTF-8 when it
    /// reads as UTF-8, else Windows' own code page (ANSI). Binary: NUL bytes in text that isn't UTF-16.
    /// <paramref name="partial"/>: only the file's start, which may end inside a character.
    /// </summary>
    internal static (string Text, TextFormat Format, bool Binary) Decode(byte[] bytes, bool partial = false)
    {
        string encoding;
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = "utf-8-bom"; text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = "utf-16le"; text = System.Text.Encoding.Unicode.GetString(bytes, 2, (bytes.Length - 2) & ~1); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = "utf-16be"; text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1); }
        else
        {
            if (Array.IndexOf(bytes, (byte)0) >= 0) return ("", new TextFormat("utf-8", "crlf"), true);
            try
            {
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                var length = partial ? CompleteUtf8(bytes) : bytes.Length;
                text = strict.GetString(bytes, 0, length);
                encoding = "utf-8";
            }
            catch (DecoderFallbackException)
            {
                text = Ansi.GetString(bytes);
                encoding = "ansi";
            }
        }
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; } else cr++; }
            else if (text[i] == '\n') lf++;
        }
        var eol = lf > crlf && lf >= cr ? "lf" : cr > crlf && cr > lf ? "cr" : "crlf";
        var normal = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return (normal, new TextFormat(encoding, eol), false);
    }

    /// <summary>How many of a cut-off UTF-8 start are whole characters.</summary>
    private static int CompleteUtf8(byte[] bytes)
    {
        var end = bytes.Length;
        for (var back = 1; back <= 3 && end - back >= 0; back++)
        {
            var b = bytes[end - back];
            if ((b & 0xC0) == 0x80) continue;
            var need = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            return need > back ? end - back : end;
        }
        return end;
    }

    private static Encoding Ansi => AnsiEncoding.Value;

    private static readonly Lazy<Encoding> AnsiEncoding = new(() =>
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
        catch (ArgumentException) { return System.Text.Encoding.Latin1; }
    });

    internal static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf-8-bom" => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. new UTF8Encoding(false).GetBytes(text)],
        "utf-16le" => [.. new byte[] { 0xFF, 0xFE }, .. System.Text.Encoding.Unicode.GetBytes(text)],
        "utf-16be" => [.. new byte[] { 0xFE, 0xFF }, .. System.Text.Encoding.BigEndianUnicode.GetBytes(text)],
        "ansi" => Ansi.GetBytes(text),
        _ => new UTF8Encoding(false).GetBytes(text),
    };

    // ------------------------------------------------------------------ the rest

    /// <summary>
    /// A file opened from Windows or Open: the note already holding it, else a new one (one in the Notes folder is a note,
    /// any other a file from elsewhere); in front, in its tab. Null with a reason when it can't be edited here.
    /// </summary>
    internal string? Open(string path, out string? problem)
    {
        problem = null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { problem = "That isn't a file Exo can open."; return null; }
        if (!File.Exists(full)) { problem = "That file is gone."; return null; }
        var (text, format) = ReadFile(full, out problem);
        if (text is null) return null;
        lock (_gate)
        {
            var id = State.Notes.FirstOrDefault(pair => string.Equals(pair.Value.Path, full, StringComparison.OrdinalIgnoreCase)).Key;
            if (id is null)
            {
                id = NewId();
                State.Notes[id] = new NoteEntry { Path = full, Named = true, Encoding = format.Encoding, Eol = format.Eol };
            }
            if (!State.Tabs.Contains(id)) State.Tabs.Add(id);
            State.Active = id;
            TrySave();
            return id;
        }
    }

    /// <summary>
    /// The words to <paramref name="path"/> (picked in Windows' save dialog, which asked before replacing a file), written
    /// as the note's own file is; that file then opens in a tab. A file from elsewhere becomes it, its edits saved there;
    /// one of Exo's notes stays as it is beside it.
    /// </summary>
    internal string? SaveAs(string id, string path, string text, out string? problem)
    {
        problem = null;
        if (text.Length > MaxChars) { problem = "This note is too big to save."; return null; }
        lock (_gate)
        {
            if (!State.Notes.TryGetValue(id, out var entry)) { problem = "That note is gone."; return null; }
            var full = Path.GetFullPath(path);
            if (string.Equals(full, entry.Path, StringComparison.OrdinalIgnoreCase)) return Write(id, text, toFile: true, out problem) is null ? null : id;
            var copy = new NoteEntry { Path = full, Named = true, GameId = entry.GameId, Encoding = entry.Encoding, Eol = entry.Eol };
            try { WriteFile(copy, text, fresh: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Notes: save as failed: " + ex.GetType().Name);
                problem = ex is UnauthorizedAccessException ? "Windows won't let Exo save there." : "Exo could not save the file there.";
                return null;
            }
            foreach (var held in State.Notes.Where(pair => string.Equals(pair.Value.Path, full, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList())
            {
                State.Notes.Remove(held);
                State.Tabs.Remove(held);
            }
            var next = NewId();
            State.Notes[next] = copy;
            var at = State.Tabs.IndexOf(id);
            if (!Own(entry))
            {
                State.Notes.Remove(id);
                if (at >= 0) State.Tabs[at] = next; else State.Tabs.Add(next);
            }
            else if (at >= 0) State.Tabs.Insert(at + 1, next);
            else State.Tabs.Add(next);
            State.Active = next;
            TrySave();
            return next;
        }
    }

    /// <summary>The open tabs, in order, and the one in front.</summary>
    internal void Session(IReadOnlyList<string> tabs, string? active)
    {
        lock (_gate)
        {
            State.Tabs = tabs.Where(State.Notes.ContainsKey).Distinct().ToList();
            State.Active = active is not null && State.Tabs.Contains(active) ? active : State.Tabs.FirstOrDefault();
            // A file from elsewhere is a note only while it is open; its edits go with its tab (the page asked first).
            foreach (var (id, entry) in State.Notes.ToList())
                if (!State.Tabs.Contains(id) && !Own(entry)) State.Notes.Remove(id);
            TrySave();
        }
    }

    internal NoteInfo? Pin(string id, bool on, out string? problem) => Change(id, out problem, (_, entry) => { entry.Pinned = on; return null; });

    /// <summary>Back to what the file holds: a file from elsewhere's edits dropped.</summary>
    internal NoteInfo? Discard(string id, out string? problem) => Change(id, out problem, (_, entry) => { entry.Draft = null; entry.DraftAt = null; return null; });

    /// <summary>Your own name for the note's file, kept from then on (its extension stays).</summary>
    internal NoteInfo? Rename(string id, string name, out string? problem) => Change(id, out problem, (_, entry) =>
    {
        var extension = Path.GetExtension(entry.Path);
        name = name.Trim();
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length].TrimEnd();
        if (name.Length == 0) return "Type a name.";
        var clean = FileName(name);
        var target = Unique(Path.GetDirectoryName(entry.Path)!, clean, extension, entry.Path);
        if (!string.Equals(target, entry.Path, StringComparison.Ordinal))
        {
            if (File.Exists(entry.Path)) File.Move(entry.Path, target);
            entry.Path = target;
        }
        entry.Named = true;
        return null;
    });

    /// <summary>One of Exo's notes to Markdown (.md) or text (.txt): the same file, renamed.</summary>
    internal NoteInfo? SetKind(string id, bool markdown, out string? problem) => Change(id, out problem, (store, entry) =>
    {
        if (!store.Own(entry)) return "A file from elsewhere keeps its type.";
        if (IsMarkdown(entry.Path) == markdown) return null;
        var target = Unique(Path.GetDirectoryName(entry.Path)!, Path.GetFileNameWithoutExtension(entry.Path), markdown ? ".md" : ".txt", null);
        if (File.Exists(entry.Path)) File.Move(entry.Path, target);
        entry.Path = target;
        return null;
    });

    private NoteInfo? Change(string id, out string? problem, Func<NoteStore, NoteEntry, string?> change)
    {
        lock (_gate)
        {
            if (!State.Notes.TryGetValue(id, out var entry)) { problem = "That note is gone."; return null; }
            try { problem = change(this, entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Notes: change failed: " + ex.GetType().Name);
                problem = "Exo could not change the file. It may be open in another app.";
            }
            TrySave();
            return problem is null ? Info(id, entry, Peek(entry)) : null;
        }
    }

    /// <summary>The path behind a note, for the host's own Show in folder and Recycle Bin; never sent to the page.</summary>
    internal string? PathOf(string id)
    {
        lock (_gate) return State.Notes.TryGetValue(id, out var entry) ? entry.Path : null;
    }

    /// <summary>Forgets a note once its file went to the Recycle Bin.</summary>
    internal void Forget(string id)
    {
        lock (_gate)
        {
            State.Notes.Remove(id);
            TidyTabs();
            TrySave();
        }
    }

    internal bool IsOwn(string id)
    {
        lock (_gate) return State.Notes.TryGetValue(id, out var entry) && Own(entry);
    }

    private void TrySave()
    {
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Notes: notes.json not written: " + ex.GetType().Name); }
    }
}
