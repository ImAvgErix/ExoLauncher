using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record WebClientOptions
{
    public string? MusicService { get; init; }
    public string Microphone { get; init; } = "ask";
    public string Camera { get; init; } = "ask";
    public string Clipboard { get; init; } = "ask";
    public string Notifications { get; init; } = "ask";
}

internal static class WebClientPreferences
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string? _filePath;
    private static string? _dataOverride;
    private static WebClientOptions? _snapshot;
    internal static event Action? Changed;

    private static string CurrentPath()
    {
        var dataOverride = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        if (_filePath is null || !string.Equals(dataOverride, _dataOverride, StringComparison.Ordinal))
        {
            _filePath = Path.Combine(PathHelper.AppDataDir, "web-client-preferences.json");
            _dataOverride = dataOverride;
            _snapshot = null;
        }
        return _filePath;
    }

    private static WebClientOptions Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) throw new InvalidDataException("Connected-app preferences are too large.");
            var options = JsonSerializer.Deserialize<WebClientOptions>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Connected-app preferences are empty.");
            // Cider is no longer a music service; Apple Music is what it played.
            return options.MusicService == "cider" ? options with { MusicService = "apple" } : options;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    // External file edits are loaded on explicit Reload or the next app start.
    // Rendering and permission checks share this immutable committed snapshot.
    internal static WebClientOptions Read()
    {
        lock (Gate)
        {
            var path = CurrentPath();
            try { return _snapshot ??= Load(path); }
            catch { return new(); }
        }
    }

    internal static WebClientOptions Reload()
    {
        WebClientOptions next;
        lock (Gate) { next = Load(CurrentPath()); _snapshot = next; }
        PublishChanged();
        return next;
    }

    internal static WebClientOptions Save(JsonElement patch)
    {
        WebClientOptions next;
        lock (Gate)
        {
            var path = CurrentPath();
            // A failed initial read must never turn a patch into an overwrite
            // of unreadable existing preferences.
            var current = _snapshot ??= Load(path);
            string Permission(string key, string fallback)
            {
                if (!patch.TryGetProperty(key, out var value)) return fallback;
                var choice = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return choice is "ask" or "allow" or "block" ? choice : throw new ArgumentException("Choose Ask, Allow or Block.");
            }
            next = current with {
                MusicService = patch.TryGetProperty("musicService", out var music)
                    ? MusicWebPolicy.IsService(music.GetString()) ? music.GetString() : throw new ArgumentException("Choose a music service.")
                    : current.MusicService,
                Microphone = Permission("microphone", current.Microphone),
                Camera = Permission("camera", current.Camera),
                Clipboard = Permission("clipboard", current.Clipboard),
                Notifications = Permission("notifications", current.Notifications),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json));
                File.Move(temporary, path, true);
                _snapshot = next;
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
        PublishChanged();
        return next;
    }

    private static void PublishChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { /* Observers cannot undo a committed save. */ }
    }
}

/// <summary>Event-driven permission sequencing independent of the native dialog.</summary>
internal static class ConnectedPermissionRequest
{
    internal static async Task<bool> DecideAsync(
        Func<string> choice,
        Func<bool> isCurrent,
        Func<bool> isVisible,
        Func<Task> changed,
        SemaphoreSlim promptGate,
        Func<CancellationToken, Task<bool>> prompt,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var nextChange = changed();
            if (!isCurrent()) return false;
            var policy = choice();
            if (policy != "ask") return policy == "allow";
            if (!isVisible()) { await nextChange.WaitAsync(ct).ConfigureAwait(true); continue; }

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var turn = promptGate.WaitAsync(waiting.Token);
            try
            {
                if (await Task.WhenAny(turn, nextChange.WaitAsync(waiting.Token)).ConfigureAwait(true) != turn)
                {
                    waiting.Cancel();
                    try { await turn.ConfigureAwait(true); promptGate.Release(); }
                    catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                    continue;
                }
                await turn.ConfigureAwait(true);
            }
            finally { waiting.Cancel(); }

            try
            {
                if (!isCurrent()) return false;
                policy = choice();
                if (policy != "ask") return policy == "allow";
                if (!isVisible()) continue;
                var accepted = await prompt(ct).ConfigureAwait(true);
                if (!isCurrent() || !isVisible()) return false;
                policy = choice();
                return policy == "allow" || (policy == "ask" && accepted);
            }
            finally { promptGate.Release(); }
        }
    }
}
