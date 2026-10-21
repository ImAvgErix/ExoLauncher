using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Card-level artwork workflow. It is deliberately local: custom file names
/// live only in settings.json and no image is sent to a service.
/// </summary>
public sealed class GameArtworkService
{
    public sealed record MutationResult(
        bool Ok,
        bool Cancelled,
        string? Message,
        GameEntry? Game,
        long ArtRevision);

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
            AppLog.Info("Artwork refetch gameId=" + card.Id);
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

    /// <summary>
    /// Library › Refresh covers: fetches the store's art again for every card,
    /// installed or not, one at a time. Covers you chose yourself stay. Each
    /// card publishes as it lands, so the grid fills in while it runs.
    /// </summary>
    public async Task<int> RefetchAllAsync(CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        var refreshed = 0;
        try
        {
            var ids = _library.PeekCachedLibrary()
                .Where(game => !string.Equals(game.CoverSource, "custom", StringComparison.OrdinalIgnoreCase))
                .Select(game => game.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var card = EligibleCard(id, out _);
                if (card is null) continue;
                try
                {
                    await CoverArtService.RefetchComputedAsync(_library.FindVisualSources(card.Id), _library.AllSourceEntries(), ct).ConfigureAwait(false);
                    await _library.PublishArtworkChangeAsync(card.Id, recomputeComputedCovers: true, ct).ConfigureAwait(false);
                    refreshed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Debug($"Artwork refetch failed for {card.Id}: {ex.GetType().Name}"); }
            }
            AppLog.Info($"Artwork refetch all: {refreshed} of {ids.Count} covers.");
            return refreshed;
        }
        finally
        {
            _mutationGate.Release();
        }
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
}
