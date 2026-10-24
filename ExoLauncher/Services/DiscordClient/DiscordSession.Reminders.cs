using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's reminders (saved messages with a due time): Discord keeps them, the client times them. When one comes due,
/// Windows says so, as Discord's client does; the next one due within a week holds a timer.
/// </summary>
internal sealed partial class DiscordSession
{
    private readonly Lock _reminderGate = new();
    private List<(DateTimeOffset Due, string Text)> _reminders = [];
    private System.Threading.Timer? _reminderTimer;

    /// <summary>The reminders Discord sent (each { message, save_data }), the next one due timed.</summary>
    internal void RemindAt(JsonElement? reminders)
    {
        var now = DateTimeOffset.UtcNow;
        var due = new List<(DateTimeOffset, string)>();
        if (reminders is { ValueKind: JsonValueKind.Array } items)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("save_data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("due_at", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when > now)
                    due.Add((when, ReminderText(item, data)));
        lock (_reminderGate)
        {
            _reminders = [.. due.OrderBy(r => r.Item1)];
            ArmReminder();
        }
    }

    // After READY, and whenever a message is saved or let go elsewhere: what's due is asked again.
    private async Task LoadRemindersAsync()
    {
        try
        {
            var reply = await Rest.SavedAsync(CancellationToken.None).ConfigureAwait(false);
            if (reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("reminders", out var reminders)) RemindAt(reminders);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Warn("Discord reminders: not read (" + ex.GetType().Name + ").");
        }
    }

    // Under _reminderGate: a timer for the next reminder due within a week (later ones are timed after the next read).
    private void ArmReminder()
    {
        _reminderTimer?.Dispose();
        _reminderTimer = null;
        if (_reminders.Count == 0) return;
        var wait = _reminders[0].Due - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.FromDays(7)) return;
        _reminderTimer = new System.Threading.Timer(_ => FireReminder(), null, wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
    }

    private void FireReminder()
    {
        string text;
        lock (_reminderGate)
        {
            if (_reminders.Count == 0) return;
            text = _reminders[0].Text;
            _reminders.RemoveAt(0);
            ArmReminder();
        }
        AppLog.Info("Discord client: a reminder came due.");
        DiscordNotifier.ShowApp("Reminder", text);
    }

    // "Author: what they wrote", from Discord's summaries of the saved message (or the message itself: Discord sends
    // the summaries empty).
    internal static string ReminderText(JsonElement item, JsonElement data)
    {
        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        var message = item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var author = Text(data, "author_summary") ?? (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("author", out var a) ? Text(a, "global_name") ?? Text(a, "username") : null);
        var words = Text(data, "message_summary") ?? (message.ValueKind == JsonValueKind.Object ? Text(message, "content") : null);
        var line = string.Join(": ", new[] { author, words }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return line.Length == 0 ? "A message you saved for later" : line.Length > 200 ? line[..200] + "…" : line;
    }
}
