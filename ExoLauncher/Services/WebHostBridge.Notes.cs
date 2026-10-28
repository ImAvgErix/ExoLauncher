using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Notes (Captures › Notes), over <see cref="NoteStore"/>: notes.list, notes.read, notes.save (a new note when it has
/// no id, linked to the game running), notes.act (pin, rename, Markdown or text, discard edits, Show in folder, Recycle
/// Bin), notes.session (the open tabs), notes.open and notes.saveAs (Windows' pickers), notes.pending (a file Windows
/// opened with Exo), notes.spell (Windows' spell checker) and notes.textFiles (Exo offered for text files, asked first).
/// The page sees ids, names and words, never a path.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>A file Windows opened with Exo before the page asked (Exo starting with it); taken by notes.pending.</summary>
    private static string? _pendingNote;

    internal void PostNotesEvent(string name, object? data) => PostEvent(name, data);

    /// <summary>A file Windows asked Exo to open (--note): in Notes, in front, now or once the page is up.</summary>
    internal void OpenNoteFromWindows(string path)
    {
        var id = NoteStore.Shared.Open(path, out var problem);
        if (id is not null) Volatile.Write(ref _pendingNote, id);
        PostEvent("notes.open", new { id, message = problem });
    }

    internal static void QueueNoteFromWindows(string path)
    {
        var id = NoteStore.Shared.Open(path, out var problem);
        if (id is not null) Volatile.Write(ref _pendingNote, id);
        else if (problem is not null) AppLog.Info("Notes: a file Windows opened with Exo was not opened: " + problem);
    }

    private static object NotesPending() => new { id = Interlocked.Exchange(ref _pendingNote, null) };

    private static object NotesList(string? query)
    {
        try
        {
            var (notes, tabs, active) = NoteStore.Shared.List(query);
            return new { ok = true, notes, tabs, active };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Notes: list failed: " + ex.GetType().Name);
            return new { ok = false, message = "Exo could not read the Notes folder." };
        }
    }

    private static object NotesRead(string? id)
    {
        if (id is null) return new { ok = false, message = "Nothing to open." };
        if (NoteStore.Shared.Read(id, out var problem) is not { } read) return new { ok = false, message = problem };
        return new { ok = true, text = read.Text, note = read.Info };
    }

    /// <summary>A new note when it has no id (the running game's, if one runs); else its words, to its file or kept.</summary>
    private static object NotesSave(JsonElement p, bool hasParams)
    {
        var text = ReadString(p, hasParams, "text") ?? "";
        // A new note's id is JSON null, which ReadString gives as "".
        var id = ReadString(p, hasParams, "id") is { Length: > 0 } given ? given : null;
        string? problem;
        var note = id is null
            ? NoteStore.Shared.Create(text, ReadBool(p, hasParams, "markdown") == true, RunningGame(), out problem)
            : NoteStore.Shared.Write(id, text, ReadBool(p, hasParams, "toFile") == true, out problem);
        return new { ok = note is not null, note, message = problem };
    }

    private static string? RunningGame()
    {
        try { return MainWindow.RunningLibraryGames().Keys.FirstOrDefault(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private async Task<object> NotesActAsync(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id") ?? "";
        var action = ReadString(p, hasParams, "action");
        string? problem;
        switch (action)
        {
            case "pin" or "unpin":
                return Answer(NoteStore.Shared.Pin(id, action == "pin", out problem), problem);
            case "markdown" or "text":
                return Answer(NoteStore.Shared.SetKind(id, action == "markdown", out problem), problem);
            case "rename":
                return Answer(NoteStore.Shared.Rename(id, ReadString(p, hasParams, "name") ?? "", out problem), problem);
            case "discard":
                return Answer(NoteStore.Shared.Discard(id, out problem), problem);
            case "reveal":
                var shown = NoteStore.Shared.PathOf(id);
                if (shown is null || !File.Exists(shown)) return new { ok = false, message = "That file is gone." };
                try
                {
                    using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + shown + "\"") { UseShellExecute = true });
                    return new { ok = true };
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    AppLog.Warn("Notes: Show in folder failed: " + ex.GetType().Name);
                    return new { ok = false, message = "Exo could not open the folder." };
                }
            case "folder":
                try
                {
                    var folder = NoteStore.Shared.Folder;
                    Directory.CreateDirectory(folder);
                    using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
                    return new { ok = true };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    AppLog.Warn("Notes: Notes folder did not open: " + ex.GetType().Name);
                    return new { ok = false, message = "Exo could not open the Notes folder." };
                }
            case "delete":
                // Only Exo's own notes go to the Recycle Bin; a file from elsewhere is only closed.
                if (!NoteStore.Shared.IsOwn(id) || NoteStore.Shared.PathOf(id) is not { } path) return new { ok = false, message = "Only notes in the Notes folder can be deleted here." };
                var gone = !File.Exists(path) || await Task.Run(() => MainWindow.RecycleFile(path)).ConfigureAwait(true);
                if (!gone) return new { ok = false, message = "Windows could not move it to the Recycle Bin." };
                NoteStore.Shared.Forget(id);
                return new { ok = true };
            default:
                return new { ok = false, message = "Unknown action." };
        }

        static object Answer(NoteInfo? note, string? problem) => new { ok = note is not null, note, message = problem };
    }

    private static object NotesSession(JsonElement p, bool hasParams)
    {
        var tabs = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("tabs", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(tab => tab.ValueKind == JsonValueKind.String).Select(tab => tab.GetString()!).Take(200).ToList()
            : [];
        NoteStore.Shared.Session(tabs, ReadString(p, hasParams, "active"));
        return new { ok = true };
    }

    /// <summary>Any text file, from Windows' picker, as a tab.</summary>
    private async Task<object> NotesOpenAsync()
    {
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.FileTypeFilter.Add("*");
            return (await picker.PickSingleFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        var id = await Task.Run(() => NoteStore.Shared.Open(picked.Path, out var problem) is { } opened ? (opened, (string?)null) : (null, problem)).ConfigureAwait(true);
        return new { ok = id.Item1 is not null, id = id.Item1, message = id.Item2 };
    }

    /// <summary>The words to a file you pick (Windows asks before replacing one); that file then takes the tab's place.</summary>
    private async Task<object> NotesSaveAsAsync(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id") ?? "";
        var text = ReadString(p, hasParams, "text") ?? "";
        if (NoteStore.Shared.PathOf(id) is not { } from) return new { ok = false, message = "That note is gone." };
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            var extension = Path.GetExtension(from) is { Length: > 1 } ext ? ext : ".txt";
            picker.FileTypeChoices.Add(extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? "Markdown" : "Text", [extension]);
            if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) picker.FileTypeChoices.Add("Text", [".txt"]);
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(from);
            return (await picker.PickSaveFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        var result = await Task.Run(() => NoteStore.Shared.SaveAs(id, picked.Path, text, out var problem) is { } saved ? (saved, (string?)null) : (null, problem)).ConfigureAwait(true);
        return new { ok = result.Item1 is not null, id = result.Item1, message = result.Item2 };
    }

    /// <summary>A picker on the window's thread; Failed when it could not open.</summary>
    private async Task<(string? Path, bool Failed)> OnUiThread(Func<Task<string?>> pick)
    {
        var done = new TaskCompletionSource<(string?, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunAsync()
        {
            try
            {
                if (App.MainAppWindow is null) { done.TrySetResult((null, true)); return; }
                done.TrySetResult((await pick(), false));
            }
            catch (Exception ex) { AppLog.Warn("Notes: file picker: " + ex.GetType().Name); done.TrySetResult((null, true)); }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(() => _ = RunAsync());
        else _ = RunAsync();
        return await done.Task.ConfigureAwait(true);
    }

    /// <summary>A right-clicked word: spelled right, or Windows' suggestions; add puts it in Windows' own word list.</summary>
    private static async Task<object> NotesSpellAsync(JsonElement p, bool hasParams)
    {
        var word = (ReadString(p, hasParams, "word") ?? "").Trim();
        if (ReadBool(p, hasParams, "add") == true)
            return new { ok = await Task.Run(() => NoteSpelling.Add(word)).ConfigureAwait(true) };
        var suggestions = await Task.Run(() => NoteSpelling.Suggest(word)).ConfigureAwait(true);
        return new { ok = true, wrong = suggestions is not null, suggestions = suggestions ?? [] };
    }

    /// <summary>
    /// state; offer (asked in Exo's own window first): Exo in Open with and Default apps for .txt, .md and .log, then
    /// Windows' Default apps page on Exo Notes, where you choose; withdraw: every key taken back.
    /// </summary>
    private async Task<object> NotesTextFilesAsync(string? action)
    {
        if (action == "offer")
        {
            if (Environment.ProcessPath is not { } exe) return new { ok = false, message = "Exo could not find its own program." };
            if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync("Open text files with Exo?",
                    "Exo will add itself to Open with and Default apps for .txt, .md and .log files, for you only. " +
                    "Windows then opens Default apps, where you choose Exo Notes for each type.", "Continue").ConfigureAwait(true))
                return TextFilesState(declined: true);
            if (!await Task.Run(() => NoteTextFiles.Offer(exe)).ConfigureAwait(true)) return TextFilesState(message: "Windows didn't let Exo add itself.");
            return TextFilesState(message: NoteTextFiles.OpenDefaultApps() ? null : "Exo is in Open with now. Windows' Default apps didn't open.");
        }
        if (action == "settings") return TextFilesState(message: NoteTextFiles.OpenDefaultApps() ? null : "Windows' Default apps didn't open.");
        if (action == "withdraw")
        {
            if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync("Stop opening text files with Exo?",
                    "Exo will take itself out of Open with and Default apps. Text files you set to open with Exo will ask which app to use.", "Stop").ConfigureAwait(true))
                return TextFilesState(declined: true);
            await Task.Run(NoteTextFiles.Withdraw).ConfigureAwait(true);
        }
        return TextFilesState();

        static object TextFilesState(bool declined = false, string? message = null)
        {
            var (offered, isDefault) = NoteTextFiles.State();
            return new { ok = message is null && !declined, declined, offered, isDefault, message };
        }
    }
}
