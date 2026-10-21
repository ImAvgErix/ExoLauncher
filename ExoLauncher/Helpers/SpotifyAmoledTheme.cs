using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExoLauncher.Helpers;

internal static class SpotifyAmoledTheme
{
    private static readonly byte[] Marker = "exo-amoled"u8.ToArray();
    private static readonly byte[] PatchHeader = "\n/* exo-amoled:"u8.ToArray();
    private static readonly byte[] Suffix = "\n/* exo-amoled:v9 */\n"u8.ToArray();
    private static readonly byte[] Token = "--background-base"u8.ToArray();
    private static readonly byte[] CssBytes = System.Text.Encoding.UTF8.GetBytes(Css);

    /// The block appended to each patched stylesheet: the fixed rules plus the
    /// wash overrides derived from this build's own (unpatched) stylesheets.
    private static byte[] BuildPatch(IEnumerable<byte[]> originalStyles) =>
        [.. Suffix, .. CssBytes, .. System.Text.Encoding.UTF8.GetBytes(
            SpotifyGradients.Overrides(originalStyles.Select(style => System.Text.Encoding.UTF8.GetString(style))))];
    private const long MaxArchiveBytes = 256L << 20;
    private const long MaxCssBytes = 16L << 20;
    private const long MaxAllCssBytes = 64L << 20;
    private const long MaxExpandedBytes = 512L << 20;
    private const int MaxEntries = 20000;

    /// Flatten Spotify's base surfaces and ink. The dark theme carries these
    /// tokens on .encore-dark-theme plus the dark "set" classes that
    /// re-declare them closer to the panels (muted-accent, app-frame and
    /// inverted-dark); an inherited :root value never reaches those.
    /// The page is #000. Raised surfaces (cards, menus, dialogs, the
    /// now-playing panel's cards) are #0d0d0d and get Exo's hairline from
    /// <see cref="SpotifyGradients"/>; filled controls (chips, the search
    /// field, secondary buttons, progress tracks) are #141414, so none of
    /// them becomes an invisible box on black. Hover is #141414 (#1c1c1c on
    /// a raised surface), dividers #1c1c1c. Spotify's green stays where it
    /// marks state (play, now playing, on). Artwork is never touched.
    ///
    /// Per-page color washes are inline variables (an artwork's extracted
    /// color, the lyrics palette); a stylesheet !important beats an inline
    /// value, so the elements carrying them are pinned to black here. The
    /// gradient rules themselves are hashed per build and come from
    /// <see cref="SpotifyGradients"/>. Panels and menus get Exo's hairline
    /// instead of a lighter card. Glass blur is off and motion is static
    /// (<see cref="StaticMotion"/>), so Spotify draws less at all times;
    /// playback runs in Spotify's host process and never depends on it.
    internal const string Css = """
        :root, .encore-dark-theme, .encore-dark-theme .encore-base-set,
        .encore-dark-theme .encore-muted-accent-set,
        .encore-dark-theme .encore-app-frame-set,
        .encore-dark-theme .encore-inverted-dark-set {
          --background-base:#000!important;
          --background-highlight:#141414!important;
          --background-elevated-base:#0d0d0d!important;
          --background-elevated-highlight:#1c1c1c!important;
          --background-elevated-press:#141414!important;
          --background-tinted-base:#141414!important;
          --background-tinted-highlight:#1c1c1c!important;
          --background-tinted-press:#242424!important;
          --background-unsafe-for-small-text-base:#000!important;
          --background-unsafe-for-small-text-elevated-base:#0d0d0d!important;
          --background-unsafe-for-small-text-tinted-base:#141414!important;
          --decorative-subdued:#1c1c1c!important;
          --text-base:#eee!important;
          --text-subdued:#999!important;
        }
        [style*="--background-color"], [style*="--extracted-background-color"] {
          --background-color:#000!important;
          --extracted-background-color:#000!important;
        }
        [style*="--lyrics-color-background"] {
          --lyrics-color-background:#000!important;
          --lyrics-color-active:#eee!important;
          --lyrics-color-inactive:#777!important;
        }
        .Root__nav-bar, .Root__main-view, .Root__right-sidebar {
          outline:1px solid #1c1c1c!important; outline-offset:-1px;
        }
        [role="menu"], [role="tooltip"] { outline:1px solid #242424!important; outline-offset:-1px; }
        html, body, .Root, .Root__top-container, .Root__main-view, .Root__nav-bar,
        .Root__left-sidebar, .Root__right-sidebar, .Root__now-playing-bar, .Root__globalNav,
        .main-view-container, .main-navBar-navBar, .main-nowPlayingBar-nowPlayingBar,
        .main-yourLibraryX-libraryRootlist, .main-topBar-container,
        [data-testid="topbar"], [data-testid="now-playing-bar"], [data-testid="right-sidebar"],
        [data-testid="left-sidebar"], [data-testid="global-nav-bar"],
        [data-testid="playlist-page"], [data-testid="album-page"], [data-testid="artist-page"] {
          background-color:#000!important;
          background-image:none!important;
        }
        .main-topBar-background, .main-entityHeader-backgroundColor,
        .main-entityHeader-background, [data-testid="action-bar-background"], [data-testid="background-overlay"] {
          background:#000!important;
        }
        """ + Hidden + """
         {
          display:none!important;
        }
        :is(div, section, aside, li):has(> :is(
        """ + Hidden + """
        ):only-child),
        :is(div, section, aside, li):has(> :only-child > :is(
        """ + Hidden + """
        ):only-child) {
          display:none!important;
        }
        *, *::before, *::after { backdrop-filter:none!important; -webkit-backdrop-filter:none!important; }
        """ + "\n" + StaticMotion.Css;

    /// Promos and upsells Optimize removes. A wrapper that holds nothing but
    /// one of them (a grid cell, a panel section, a top-bar slot) goes with
    /// it, up to two levels, so no empty box, gap or padded slot is left.
    private const string Hidden = """
        [data-testid="home-ads-container"], [data-testid="home-ad-card"], [data-testid="home-dsa-message-banner"],
        [data-testid="embedded-ad-carousel"], [data-testid="ad-companion-card"], [data-testid="canvas-ad-container"],
        [data-testid="upsell-download-app-button"], [data-testid="artist-pick-merch-container"],
        [data-testid="concert-promo"], [data-testid="concert-recommendations"],
        [data-testid="whats-new-feed-button"], a[href="/content-feed"], a[href="/download"], a[href*="spotify.com/premium"]
        """;

    /// Canvas (the looping video behind a song) decodes video for as long as
    /// the now-playing view shows it. Its surface is the only one Spotify
    /// marks data-canvas-surface; a canvas video is paused the moment it
    /// starts and keeps its first frame. Episodes and video podcasts play
    /// elsewhere and are never touched. Registered through the same DevTools
    /// session as SpotifyControls.Script, so only a Spotify Exo launched runs it.
    internal const string OptimizeScript = """
        (function(){
        if (window.__exoOptimize) return; window.__exoOptimize = true;
        document.addEventListener('play', function (e) {
          var v = e.target;
          if (v && v.tagName === 'VIDEO' && v.closest && v.closest('[data-canvas-surface]')) v.pause();
        }, true);
        })();
        """;

    /// Spotify's own preferences Exo sets: no home-page takeover, no promoted
    /// songs, no Windows notification on every track change (Exo's mini
    /// player already shows it), and no hardware acceleration (AppPrefs). Added only where the line is absent;
    /// Spotify rereads prefs at start, so this runs while it is stopped.
    private static readonly string[] Prefs = ["ui.hide_hpto=true", "ui.hide_promoted_songs=true", "ui.track_os_notifications_enabled=false"];

    /// Lines earlier Exo builds wrote under a key Spotify does not read
    /// (Spotify.dll 1.3.5 has ui.track_os_notifications_enabled, not
    /// ui.track_notifications_enabled): taken out on every pass.
    private static readonly string[] DeadPrefs = ["ui.track_notifications_enabled=false"];

    /// The app-wide prefs beside Users\: hardware acceleration off. Exo's
    /// Music room draws everything the user sees and Spotify's own window
    /// stays hidden, so its GPU process only costs memory. Measured hidden
    /// under Exo a minute after start: 864 MB private for the tree with it on,
    /// 707 MB off (GPU process 173 MB to 34 MB); the room and playback work.
    private static readonly string[] AppPrefs = ["ui.hardware_acceleration=false"];

    internal static void SetPrefs(string usersDirectory)
    {
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(usersDirectory)) is { } root &&
            File.Exists(Path.Combine(root, "prefs")))
            MergePrefs(Path.Combine(root, "prefs"), AppPrefs);
        if (!Directory.Exists(usersDirectory)) return;
        foreach (var prefs in Directory.EnumerateFiles(usersDirectory, "prefs", SearchOption.AllDirectories))
            MergePrefs(prefs, Prefs);
    }

    private static void MergePrefs(string prefs, string[] wanted)
    {
        try
        {
            var text = File.ReadAllText(prefs);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var original = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
            var lines = original.Where(line => !DeadPrefs.Contains(line)).ToList();
            var next = lines.Concat(wanted.Where(pref => !lines.Any(line => line.StartsWith(pref[..(pref.IndexOf('=') + 1)], StringComparison.Ordinal)))).ToList();
            if (next.SequenceEqual(original)) return;
            var temporary = prefs + ".exo-tmp";
            File.WriteAllText(temporary, string.Join(newline, next) + newline);
            File.Move(temporary, prefs, true);
        }
        catch (Exception error) { AppLog.Warn("Spotify preferences could not be updated: " + error.Message); }
    }

    internal static bool Apply(string appsDirectory)
    {
        var archive = Path.Combine(appsDirectory, "xpui.spa");
        if (!File.Exists(archive)) return false;
        if (new FileInfo(archive).Length > MaxArchiveBytes)
            throw new InvalidDataException("xpui.spa is larger than the supported archive size.");
        var backup = archive + ".exo-original";
        var statePath = archive + ".exo-state.json";
        var state = ReadState(statePath);
        var snapshot = TempName(archive);
        try
        {
            using (var live = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var boundedLive = new ByteBudgetStream(live, MaxArchiveBytes, leaveOpen: true))
            using (var copy = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                boundedLive.CopyTo(copy);
            var snapshotHash = HashFile(snapshot);
            var styles = ReadSnapshot(snapshot);
            var marked = new List<CssSource>();
            foreach (var entry in styles)
                if (entry.Content.AsSpan().IndexOf(Marker) >= 0) marked.Add(entry);
            if (marked.Count != 0)
            {
                if (state?.OriginalHash is null || state.PatchedHash is null ||
                    snapshotHash != state.PatchedHash || !File.Exists(backup) ||
                    HashFile(backup) != state.OriginalHash)
                    throw new InvalidDataException("xpui.spa carries an unverifiable exo patch; left untouched.");
                // The snapshot hash already proves the archive is exactly what
                // Exo wrote; stripping each marked entry back to its recorded
                // original proves the patch only ever appended bytes.
                var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in marked)
                {
                    var at = entry.Content.AsSpan().LastIndexOf(PatchHeader);
                    if (at <= 0)
                        throw new InvalidDataException("xpui.spa patch boundary is unreadable; left untouched.");
                    var original = entry.Content[..at];
                    if (!ReadCssEntry(backup, entry.Name).SequenceEqual(original))
                        throw new InvalidDataException("xpui.spa patch does not match its recorded original; left untouched.");
                    originals[entry.Name] = original;
                }
                var current = BuildPatch(styles.Select(entry =>
                    originals.TryGetValue(entry.Name, out var original) ? original : entry.Content));
                if (marked.All(entry => entry.Content.AsSpan().EndsWith(current))) return true;
                // A patch from an older Css build is verified against the same
                // backup, then rewritten with the current block in place.
                var refreshTemp = TempName(archive);
                var refreshStateTemp = TempName(statePath);
                try
                {
                    var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (var pair in originals)
                        replacements[pair.Key] = pair.Value.Concat(current).ToArray();
                    WritePatched(snapshot, refreshTemp, replacements);
                    File.WriteAllText(refreshStateTemp, StateJson(state.OriginalHash!, HashFile(refreshTemp)));
                    Promote(archive, backup, statePath, refreshTemp, null, refreshStateTemp, snapshotHash, snapshot);
                }
                finally
                {
                    TryDelete(refreshTemp);
                    TryDelete(refreshStateTemp);
                }
                return true;
            }
            var patch = BuildPatch(styles.Select(entry => entry.Content));
            var patched = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in styles)
            {
                if (entry.Content.AsSpan().IndexOf(Token) < 0) continue;
                if (entry.Content.Length + patch.Length > MaxCssBytes)
                    throw new InvalidDataException("a Spotify stylesheet would exceed the supported size once patched.");
                patched[entry.Name] = entry.Content.Concat(patch).ToArray();
            }
            if (patched.Count == 0)
                throw new InvalidDataException("xpui.spa has no stylesheet carrying Spotify's background tokens.");

            var patchedTemp = TempName(archive);
            var backupTemp = TempName(backup);
            var stateTemp = TempName(statePath);
            try
            {
                WritePatched(snapshot, patchedTemp, patched);
                var patchedHash = HashFile(patchedTemp);
                File.WriteAllText(stateTemp, StateJson(snapshotHash, patchedHash));
                var reuseBackup = state?.OriginalHash is not null && snapshotHash == state.OriginalHash &&
                    File.Exists(backup) && HashFile(backup) == state.OriginalHash;
                Promote(archive, backup, statePath, patchedTemp,
                    reuseBackup ? null : backupTemp, stateTemp, snapshotHash, snapshot);
            }
            finally
            {
                TryDelete(patchedTemp);
                TryDelete(backupTemp);
                TryDelete(stateTemp);
            }
            return true;
        }
        finally { TryDelete(snapshot); }
    }

    private sealed record CssSource(string Name, byte[] Content);

    private sealed record PatchState(string? OriginalHash, string? PatchedHash);

    private static string StateJson(string originalHash, string patchedHash) =>
        "{\"originalHash\":\"" + originalHash + "\",\"patchedHash\":\"" + patchedHash + "\"}";

    /// Move backup (when a fresh one was staged), state, then the archive into
    /// place; on any failure put back what was moved aside and rethrow.
    private static void Promote(string archive, string backup, string statePath,
        string patchedTemp, string? backupTemp, string stateTemp, string snapshotHash, string snapshot)
    {
        string? backupRollback = null;
        string? stateRollback = null;
        var backupPromoted = false;
        var statePromoted = false;
        var committed = false;
        try
        {
            if (HashFile(archive) != snapshotHash)
                throw new IOException("xpui.spa changed while it was being patched.");
            if (backupTemp is not null)
            {
                File.Copy(snapshot, backupTemp);
                backupRollback = MoveAsideIfExists(backup);
            }
            stateRollback = MoveAsideIfExists(statePath);
            if (backupTemp is not null) { File.Move(backupTemp, backup); backupPromoted = true; }
            File.Move(stateTemp, statePath); statePromoted = true;
            if (HashFile(archive) != snapshotHash)
                throw new IOException("xpui.spa changed while it was being patched.");
            File.Move(patchedTemp, archive, true);
            committed = true;
        }
        catch
        {
            if (backupPromoted) TryDelete(backup);
            if (backupRollback is not null) File.Move(backupRollback, backup);
            if (statePromoted) TryDelete(statePath);
            if (stateRollback is not null) File.Move(stateRollback, statePath);
            throw;
        }
        finally
        {
            if (committed)
            {
                TryDelete(backupRollback);
                TryDelete(stateRollback);
            }
        }
    }

    private static PatchState? ReadState(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var bounded = new ByteBudgetStream(stream, 8192, leaveOpen: true);
            using var json = JsonDocument.Parse(bounded);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            return new PatchState(ReadHash(json.RootElement, "originalHash"),
                                  ReadHash(json.RootElement, "patchedHash"));
        }
        catch { return null; }
    }

    private static string? ReadHash(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant() : null;

    /// Every stylesheet in the archive, read with per-entry and total bounds.
    /// Newer Spotify builds split or rename xpui.css, so the patch target is
    /// whichever stylesheet carries the provider's background tokens.
    private static List<CssSource> ReadSnapshot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("xpui.spa has more entries than the supported limit.");
        long total = 0;
        long cssTotal = 0;
        var styles = new List<CssSource>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - total)
                throw new InvalidDataException("xpui.spa entries exceed the supported expanded size.");
            total += entry.Length;
            if (!entry.FullName.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length > MaxCssBytes || entry.Length > MaxAllCssBytes - cssTotal)
                throw new InvalidDataException("a Spotify stylesheet is larger than the supported size.");
            cssTotal += entry.Length;
            using var source = entry.Open();
            using var bounded = new ByteBudgetStream(source, MaxCssBytes, leaveOpen: true);
            using var memory = new MemoryStream();
            bounded.CopyTo(memory);
            if (bounded.Position != entry.Length || source.ReadByte() >= 0)
                throw new InvalidDataException("a Spotify stylesheet entry is truncated.");
            styles.Add(new CssSource(entry.FullName, memory.ToArray()));
        }
        return styles;
    }

    private static byte[] ReadCssEntry(string path, string name) =>
        ReadEntryOrNull(path, name)
            ?? throw new InvalidDataException("recorded original archive is missing the patched stylesheet.");

    private static byte[]? ReadEntryOrNull(string path, string name)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? css = null;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.Equals(name, StringComparison.Ordinal)) continue;
            css ??= entry;
        }
        if (css is null) return null;
        if (css.Length > MaxCssBytes)
            throw new InvalidDataException("recorded original archive has an oversized stylesheet.");
        using var source = css.Open();
        using var bounded = new ByteBudgetStream(source, MaxCssBytes, leaveOpen: true);
        using var memory = new MemoryStream();
        bounded.CopyTo(memory);
        if (bounded.Position != css.Length || source.ReadByte() >= 0)
            throw new InvalidDataException("recorded original archive has a truncated stylesheet.");
        return memory.ToArray();
    }

    private static void WritePatched(string sourcePath, string target, IReadOnlyDictionary<string, byte[]> replacements)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var boundedOutput = new ByteBudgetStream(output, MaxArchiveBytes, leaveOpen: true);
        using (var patched = new ZipArchive(boundedOutput, ZipArchiveMode.Create, leaveOpen: true))
        {
            long remaining = MaxExpandedBytes;
            foreach (var pair in replacements)
            {
                if (pair.Value.Length > remaining)
                    throw new InvalidDataException("xpui.spa exceeds the supported size.");
                remaining -= pair.Value.Length;
            }
            foreach (var entry in zip.Entries)
            {
                var copy = patched.CreateEntry(entry.FullName);
                copy.LastWriteTime = entry.LastWriteTime;
                copy.ExternalAttributes = entry.ExternalAttributes;
                using var sink = copy.Open();
                if (replacements.TryGetValue(entry.FullName, out var replacement))
                {
                    using var limited = new ByteBudgetStream(sink, MaxCssBytes, leaveOpen: true);
                    limited.Write(replacement, 0, replacement.Length);
                }
                else
                {
                    if (entry.Length < 0 || entry.Length > remaining)
                        throw new InvalidDataException("xpui.spa entry exceeds the supported size.");
                    remaining -= entry.Length;
                    using var original = entry.Open();
                    using var limited = new ByteBudgetStream(original, entry.Length, leaveOpen: true);
                    limited.CopyTo(sink);
                    if (limited.Position != entry.Length || original.ReadByte() >= 0)
                        throw new InvalidDataException("xpui.spa entry is truncated.");
                }
            }
        }
        boundedOutput.Flush();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string TempName(string file) => file + ".tmp-" + Guid.NewGuid().ToString("N");

    private static string? MoveAsideIfExists(string file)
    {
        if (!File.Exists(file)) return null;
        var rollback = TempName(file);
        File.Move(file, rollback);
        return rollback;
    }

    private static void TryDelete(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); } catch { }
    }
}
