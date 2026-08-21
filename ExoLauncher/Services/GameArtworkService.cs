using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Card-level artwork workflow. It is deliberately local: custom file names
/// live only in settings.json and no image or diagnostic is sent to a service.
/// </summary>
public sealed class GameArtworkService
{
    public const string IssueUrl = "https://github.com/ImAvgErix/ExoLauncher/issues/new";
    public const int MaxReportBytes = 4 * 1024;

    public sealed record MutationResult(
        bool Ok,
        bool Cancelled,
        string? Message,
        GameEntry? Game,
        long ArtRevision);

    public sealed record ReportResult(bool Ok, string? Message, string? Diagnostics);

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly GameCoverImageStore _images;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    internal GameArtworkService(
        LibraryService library,
        SettingsService settings,
        GameCoverImageStore? images = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _images = images ?? new GameCoverImageStore();
    }

    /// <summary>
    /// The path must come from WebHostBridge's native file picker. The React RPC
    /// accepts only a game id and has no parameter capable of carrying a path.
    /// </summary>
    internal async Task<MutationResult> ReplaceAsync(
        string gameId,
        string pickedPath,
        CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var card = EligibleCard(gameId, out var error);
            if (card is null) return Failure(error!);
            var ids = LibraryService.SourceIdsFor(card);
            var previous = ids
                .Select(id => _settings.GetCustomCoverImage([id]))
                .Where(name => name is not null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var imported = await _images.ImportAsync(pickedPath, ct).ConfigureAwait(false);
            if (!imported.Ok || imported.FileName is null)
                return Failure(imported.Message ?? "That cover could not be used.");

            try
            {
                _settings.SetCustomCoverImages(ids, imported.FileName);
            }
            catch
            {
                if (imported.Created && !_settings.CustomCoverImageFiles().Contains(imported.FileName))
                    _images.Delete(imported.FileName);
                throw;
            }

            DeleteUnreferenced(previous);
            var changed = await _library.PublishArtworkChangeAsync(
                card.Id,
                recomputeComputedCovers: false,
                ct).ConfigureAwait(false);
            return Success(changed, "Cover replaced.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Custom cover replace failed: " + ex.GetType().Name);
            return Failure("Cover could not be replaced.");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<MutationResult> ResetAsync(string gameId, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var card = EligibleCard(gameId, out var error);
            if (card is null) return Failure(error!);
            var ids = LibraryService.SourceIdsFor(card);
            var previous = ids
                .Select(id => _settings.GetCustomCoverImage([id]))
                .Where(name => name is not null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _settings.SetCustomCoverImages(ids, null);
            DeleteUnreferenced(previous);
            var changed = await _library.PublishArtworkChangeAsync(
                card.Id,
                recomputeComputedCovers: true,
                ct).ConfigureAwait(false);
            return Success(changed, "Cover reset.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Custom cover reset failed: " + ex.GetType().Name);
            return Failure("Cover could not be reset.");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<MutationResult> RefetchAsync(string gameId, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var card = EligibleCard(gameId, out var error);
            if (card is null) return Failure(error!);
            var sources = _library.FindVisualSources(card.Id);
            await CoverArtService.RefetchComputedAsync(
                sources,
                _library.AllSourceEntries(),
                ct).ConfigureAwait(false);
            var changed = await _library.PublishArtworkChangeAsync(
                card.Id,
                recomputeComputedCovers: true,
                ct).ConfigureAwait(false);
            return Success(changed, "Artwork refreshed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Artwork refetch failed: " + ex.GetType().Name);
            return Failure("Artwork could not be refreshed.");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ReportResult BuildReport(string gameId)
    {
        var card = EligibleCard(gameId, out var error);
        if (card is null) return new ReportResult(false, error, null);
        var localFile = CoverArtService.TryResolveLocalFile(card.CoverUrl);
        var dimensions = localFile is null ? null : CoverArtService.ReadImageSize(localFile);
        var sourceIds = LibraryService.SourceIdsFor(card);
        var lines = new[]
        {
            "Exo Launcher artwork report",
            "Title: " + Sanitize(card.Title, 180),
            "Game id: " + Sanitize(card.Id, 220),
            "Store: " + card.Store.ToString().ToLowerInvariant(),
            "Sources: " + string.Join(", ", sourceIds.Select(id => Sanitize(id, 160))),
            "Installed: " + card.Installed.ToString().ToLowerInvariant(),
            "Owned: " + card.Owned.ToString().ToLowerInvariant(),
            "Cover source: " + Sanitize(card.CoverSource ?? "none", 40),
            "Cover kind: " + CoverKind(card.CoverUrl),
            "Dimensions: " + (dimensions is null ? "unknown" : $"{dimensions.Value.Width}x{dimensions.Value.Height}"),
            "Art revision: " + card.ArtRevision,
            "No file paths or image bytes are included.",
        };
        return new ReportResult(true, "Artwork details copied.", LimitUtf8(string.Join('\n', lines)));
    }

    private GameEntry? EligibleCard(string gameId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(gameId))
        {
            error = "Missing game id.";
            return null;
        }
        var card = _library.FindVisualCard(gameId);
        if (card is null)
        {
            error = "That title is not in your library.";
            return null;
        }
        if (string.Equals(card.Id, "local:add", StringComparison.OrdinalIgnoreCase) ||
            (!card.Owned && !card.Installed))
        {
            error = "Artwork controls are available for library titles.";
            return null;
        }
        return card;
    }

    private void DeleteUnreferenced(IEnumerable<string> fileNames)
    {
        var referenced = _settings.CustomCoverImageFiles();
        foreach (var fileName in fileNames)
        {
            if (!referenced.Contains(fileName)) _images.Delete(fileName);
        }
    }

    private static MutationResult Success(GameEntry? game, string message) => game is null
        ? Failure("The library changed while artwork was being updated.")
        : new MutationResult(true, false, message, game, game.ArtRevision);

    private static MutationResult Failure(string message) => new(false, false, message, null, 0);

    private static string CoverKind(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "none";
        if (url.Contains("/custom-cover-", StringComparison.OrdinalIgnoreCase)) return "custom-local";
        if (url.StartsWith(CoverArtService.VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
            return "computed-local";
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "official-remote" : "unavailable";
    }

    private static string Sanitize(string value, int maxChars)
    {
        var cleaned = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) || character is '/' or '\\' ? ' ' : character)
            .ToArray()).Trim();
        return cleaned.Length <= maxChars ? cleaned : cleaned[..maxChars] + "…";
    }

    private static string LimitUtf8(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxReportBytes) return value;
        var builder = new StringBuilder(value.Length);
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + runeBytes > MaxReportBytes)
                break;
            builder.Append(text);
            bytes += runeBytes;
        }
        return builder.ToString();
    }
}
