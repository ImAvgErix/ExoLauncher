using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static readonly TimeSpan GiftsFresh = TimeSpan.FromMinutes(30);
    private readonly StoreGifts _gifts = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _giftsTimer;
    private DateTimeOffset _giftsReadAt;
    private bool _giftsReading;

    /// <summary>
    /// gifts.list: the gifts Exo last read, at once. A read older than half an hour (or <paramref name="force"/>)
    /// runs again behind it and arrives as gifts.changed; after that it runs every half hour while Exo is in front
    /// and no game plays.
    /// </summary>
    private object GiftsList(bool force)
    {
        if (_giftsTimer is null)
        {
            _giftsTimer = _queue.CreateTimer();
            _giftsTimer.Interval = GiftsFresh;
            _giftsTimer.Tick += (_, _) =>
            {
                if (App.MainAppWindow?.WindowIsActive == true && !_services.Launcher.AnyGameRunning) _ = ReadGiftsAsync();
            };
            _giftsTimer.Start();
        }
        if (force || DateTimeOffset.UtcNow - _giftsReadAt > GiftsFresh) _ = ReadGiftsAsync();
        return GiftsDto(_gifts.Last);
    }

    /// <summary>Reads both stores again and tells the page. UI thread only.</summary>
    private async Task ReadGiftsAsync()
    {
        if (_giftsReading || _detached || GiftsOwner() is not { } owner) return;
        _giftsReading = true;
        try
        {
            var snapshot = await _gifts.ReadAsync(owner, _bridgeLifetime.Token).ConfigureAwait(true);
            _giftsReadAt = DateTimeOffset.UtcNow;
            PostEvent("gifts.changed", GiftsDto(snapshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Gifts: read failed: " + ex.GetType().Name);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _giftsReading = false;
        }
    }

    /// <summary>gifts.answer: accept (to the library) or decline one gift; an accepted game's store is read again so it shows.</summary>
    private async Task<object> GiftsAnswerAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var store = ReadString(p, hasParams, "store");
        var id = ReadString(p, hasParams, "id");
        var accept = ReadBool(p, hasParams, "accept") == true;
        if (store is not ("steam" or "epic") || id is null || GiftsOwner() is not { } owner)
            return new { ok = false, message = "That gift is no longer here." };
        var (ok, message) = await _gifts.AnswerAsync(owner, store, id, accept, ct).ConfigureAwait(true);
        // The page keeps the answered gift a moment with its result line; the next read leaves it out.
        if (ok && accept) _ = Task.Run(() => RefreshLibraryForGiftAsync(_bridgeLifetime.Token));
        return new { ok, message };
    }

    /// <summary>The store hands over the game within seconds: the library is read now and once more a little later.</summary>
    private async Task RefreshLibraryForGiftAsync(CancellationToken ct)
    {
        try
        {
            foreach (var wait in new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30) })
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                await _services.Library.GetLibraryAsync(force: true, fullScan: true, ct).ConfigureAwait(false);
                OnLibraryUpdated();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Gifts: library refresh after accepting failed: " + ex.GetType().Name);
        }
    }

    /// <summary>gifts.steamSignIn: Steam's own sign-in page, once, so its gifts can be read; then a read.</summary>
    private async Task<object> GiftsSteamSignInAsync(CancellationToken ct)
    {
        var (ok, message) = await _gifts.SignInSteamAsync(ct).ConfigureAwait(true);
        if (ok) _ = ReadGiftsAsync();
        return new { ok, message };
    }

    /// <summary>gifts.steamSignOut: Exo forgets its Steam website sign-in; Steam itself stays signed in.</summary>
    private async Task<object> GiftsSteamSignOutAsync(CancellationToken ct)
    {
        if (GiftsOwner() is not { } owner) return new { ok = false, message = "The app window is unavailable." };
        await _gifts.SignOutSteamAsync(owner, ct).ConfigureAwait(true);
        PostEvent("gifts.changed", GiftsDto(_gifts.Last));
        return new { ok = true, message = "Signed out of Steam's website." };
    }

    private static nint? GiftsOwner() =>
        App.MainAppWindow is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : null;

    private static object GiftsDto(StoreGifts.Snapshot snapshot) => new
    {
        ok = true,
        steamSignedIn = snapshot.SteamSignedIn,
        gifts = snapshot.Gifts.Select(g => new { store = g.Store, id = g.Id, title = g.Title, image = g.Image, sender = g.Sender, message = g.Message }),
    };
}
