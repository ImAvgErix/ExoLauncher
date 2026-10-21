using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>Where a message notification shows: nowhere, only Discord's sound, Exo's own banner, or Windows'.</summary>
internal enum NotifyRoute { None, Sound, InApp, Windows }

/// <summary>
/// Windows notifications for Exo's own Discord client, while Exo is in the background (in front, Exo's own banner
/// shows instead). Each is tagged with its message and grouped by its conversation, as Discord's are, so reading that
/// conversation (in Exo, or in another Discord app: MESSAGE_ACK) or opening it takes them all away, and deleting the
/// message takes its own. Clicking one brings Exo to the front on that conversation (<see cref="Opened"/>). The text is
/// the message's own, shortened, under the person's name and picture; it lives in Windows' notification center as
/// Discord's own would.
/// </summary>
internal static class DiscordNotifier
{
    private static readonly Lock Gate = new();
    private static bool _registered;
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(4));

    /// <summary>A notification was clicked: (guild id or null for a DM, channel id, and a thread's channel and name).</summary>
    internal static event Action<string?, string, string?, string?>? Opened;

    /// <summary>One of Exo's own notices was clicked: bring Exo up.</summary>
    internal static event Action? AppOpened;

    /// <summary>
    /// Where a message that notifies goes. Discord's own: nothing for the conversation on screen while the app is
    /// in front, nothing while a game runs with Game focus on; Streamer Mode's Disable notifications and desktop
    /// notifications off leave only the sound. Otherwise Exo's banner while Exo is in front, Windows' when it isn't.
    /// </summary>
    internal static NotifyRoute Route(bool exoInFront, string? viewing, string channelId, bool gameQuiet, bool streamerQuiet, bool desktop) =>
        exoInFront && viewing == channelId ? NotifyRoute.None
        : gameQuiet ? NotifyRoute.None
        : streamerQuiet || !desktop ? NotifyRoute.Sound
        : exoInFront ? NotifyRoute.InApp
        : NotifyRoute.Windows;

    /// <summary>
    /// Exo's own notice while it is in the background (a download that finished, failed or was
    /// cancelled), through the same Windows registration as Discord's. Clicking it brings Exo up.
    /// </summary>
    internal static void ShowApp(string title, string body)
    {
        try
        {
            if (!Ensure()) return;
            var builder = new AppNotificationBuilder()
                .AddArgument("exo", "open")
                .AddText(title)
                .AddText(body);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex) { AppLog.Debug("Exo notification not shown: " + ex.GetType().Name); }
    }

    /// <summary>A DM call ringing: grouped with that DM, so opening it takes the notice away.</summary>
    internal static void ShowCall(string title, string body, string channelId) =>
        Show(Builder(null, channelId).AddText(title).AddText(body), "call", channelId);

    /// <summary>A message, with the person's picture (fetched once into Exo's data folder) when Discord's CDN has it.</summary>
    internal static void Show(DiscordCache.Notification n) => _ = Task.Run(async () =>
    {
        var builder = Builder(n.GuildId, n.ChannelId).AddText(n.Author).AddText(n.Body);
        if (n.ParentId is { } parent) builder.AddArgument("discordParent", parent).AddArgument("discordName", n.Place?.TrimStart('#') ?? "");
        if (Where(n.Place, n.Server) is { } where) builder.SetAttributionText(where);
        if (await AvatarFileAsync(n.Avatar).ConfigureAwait(false) is { } file)
            builder.SetAppLogoOverride(new Uri(file), AppNotificationImageCrop.Circle);
        Show(builder, n.MessageId, n.ChannelId);
    });

    /// <summary>The line under a message: "#channel · Server", a group's name, or nothing for a DM.</summary>
    internal static string? Where(string? place, string? server) =>
        place is null ? server : server is null ? place : place + " · " + server;

    /// <summary>Takes away a conversation's notifications (<paramref name="messageId"/> null), or one message's.</summary>
    internal static void Clear(string channelId, string? messageId)
    {
        try
        {
            if (!Ensure()) return;
            var removing = messageId is null
                ? AppNotificationManager.Default.RemoveByGroupAsync(channelId)
                : AppNotificationManager.Default.RemoveByTagAndGroupAsync(messageId, channelId);
            _ = removing.AsTask().ContinueWith(t => AppLog.Debug("Discord notifications not removed: " + t.Exception?.InnerException?.GetType().Name),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { AppLog.Debug("Discord notifications not removed: " + ex.GetType().Name); }
    }

    private static AppNotificationBuilder Builder(string? guildId, string channelId) => new AppNotificationBuilder()
        .AddArgument("discordChannel", channelId)
        .AddArgument("discordGuild", guildId ?? "");

    private static void Show(AppNotificationBuilder builder, string tag, string group)
    {
        try
        {
            if (!Ensure()) return;
            var notification = builder.BuildNotification();
            notification.Tag = tag;
            notification.Group = group;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { AppLog.Debug("Discord notification not shown: " + ex.GetType().Name); }
    }

    // Windows draws only a local picture for an unpackaged app: the CDN's PNG of the avatar, kept by its address.
    private static async Task<string?> AvatarFileAsync(string? url)
    {
        if (url is null || !url.StartsWith("https://cdn.discordapp.com/", StringComparison.Ordinal)) return null;
        var png = url.Replace(".webp?size=256&quality=lossless", ".png?size=128", StringComparison.Ordinal);
        var dir = Path.Combine(PathHelper.AppDataDir, "discord", "avatars");
        var path = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(png)))[..24] + ".png");
        if (File.Exists(path)) return path;
        try
        {
            var bytes = await Http.GetByteArrayAsync(png).ConfigureAwait(false);
            if (bytes.Length is 0 or > 1_000_000) return null;
            Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            // A few hundred people at most: the oldest pictures go first.
            var files = new DirectoryInfo(dir).GetFiles("*.png");
            if (files.Length > 300)
                foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(100)) old.Delete();
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Ensure()
    {
        lock (Gate)
        {
            if (_registered) return true;
            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += (_, e) =>
                {
                    if (e.Arguments.ContainsKey("exo")) { AppOpened?.Invoke(); return; }
                    if (!e.Arguments.TryGetValue("discordChannel", out var channel) || channel.Length == 0) return;
                    var guild = e.Arguments.TryGetValue("discordGuild", out var g) && g.Length > 0 ? g : null;
                    var parent = e.Arguments.TryGetValue("discordParent", out var p) && p.Length > 0 ? p : null;
                    Opened?.Invoke(guild, channel, parent, parent is not null && e.Arguments.TryGetValue("discordName", out var name) ? name : null);
                };
                manager.Register();
                _registered = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("Discord notifications unavailable: " + ex.GetType().Name);
                return false;
            }
        }
    }
}
