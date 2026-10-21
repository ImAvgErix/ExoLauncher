using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>Declarative intent only: never a command, delegate, credential, or destructive action.</summary>
internal sealed record DownloadIntent
{
    public string Token { get; init; } = Guid.NewGuid().ToString("N");
    public required string GameId { get; init; }
    public required string Title { get; init; }
    public required StoreKind Store { get; init; }
    public required string SourceId { get; init; }
    public string? AccountScope { get; init; }
    public string? LaunchTarget { get; init; }
    public string? GamePath { get; init; }
    public required string Action { get; init; }
    public string? InstallRoot { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string State { get; init; } = "pending";
    public string? ReviewReason { get; init; }
}

public sealed record PausedDownload(string GameId, string Title, string Store, string Action,
    bool CanResume, string Reason);

internal sealed class PendingDownloadStore(string path)
{
    internal const int EntryLimit = 256;
    internal const int ByteLimit = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Envelope(int Version, IReadOnlyList<DownloadIntent> Intents);

    internal IReadOnlyList<DownloadIntent> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > ByteLimit) throw new InvalidDataException("Saved download queue is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), JsonOptions);
            if (envelope is null || envelope.Version != 1 || envelope.Intents is null || envelope.Intents.Count > EntryLimit)
                throw new InvalidDataException("Saved download queue format is invalid.");
            return envelope.Intents.Where(IsSafeIntent)
                .Where(intent => intent.State is "pending" or "running" or "review" or LaunchOrchestrator.HeldState)
                .DistinctBy(intent => intent.GameId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException)
        {
            // Keep the bad file for recovery and carry on with an empty queue.
            // Refusing every later save left installs and updates blocked for
            // the whole session.
            Helpers.AppLog.Warn("Saved download queue could not be read: " + ex.GetType().Name);
            Helpers.PathHelper.MoveAsideUnreadable(path);
            return [];
        }
    }

    internal bool TrySave(IReadOnlyList<DownloadIntent> intents, out string? error)
    {
        error = null;
        if (intents.Count > EntryLimit || intents.Any(intent => !IsSafeIntent(intent)))
        {
            error = "The saved download queue is full or contains an unsupported request.";
            return false;
        }
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, intents), JsonOptions);
            if (bytes.Length > ByteLimit) throw new IOException("Saved download queue is too large.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".download-intents-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = "The download queue could not be saved. Check that Exo's data folder is writable and try again.";
            Helpers.AppLog.Warn("Saved download queue write failed: " + ex.GetType().Name);
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* A private temporary file is safe to leave. */ }
        }
    }

    private static bool IsSafeIntent(DownloadIntent? intent) =>
        intent is not null && Guid.TryParseExact(intent.Token, "N", out _) &&
        !string.IsNullOrWhiteSpace(intent.GameId) && intent.GameId.Length <= 512 &&
        !string.IsNullOrWhiteSpace(intent.Title) && intent.Title.Length <= 1024 &&
        !string.IsNullOrWhiteSpace(intent.SourceId) && intent.SourceId.Length <= 128 &&
        Enum.IsDefined(intent.Store) && intent.Store != StoreKind.Local &&
        intent.Action is "install" or "update" &&
        (intent.AccountScope is null || intent.AccountScope.Length <= 512) &&
        (intent.LaunchTarget is null || intent.LaunchTarget.Length <= 4096) &&
        (intent.GamePath is null || intent.GamePath.Length <= 4096) &&
        (intent.ReviewReason is null || intent.ReviewReason.Length <= 2048) &&
        (intent.Action != "install" || intent.InstallRoot is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(intent.InstallRoot));
}
