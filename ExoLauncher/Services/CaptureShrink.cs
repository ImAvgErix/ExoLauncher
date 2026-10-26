namespace ExoLauncher.Services;

/// <summary>
/// The host's side of Exo Capture's shrink (the add-on re-encodes a finished recording to a smaller one and proves it
/// looks the same): which recordings are asked for, when, how the smaller file takes the original's place, and what a
/// refusal says. The add-on never touches the original; the swap is here, and keeps the original until the new file is in.
/// </summary>
internal static class CaptureShrink
{
    /// <summary>A recording this young may still be open in an editor or being copied.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    internal const string DoneMark = "done";

    /// <summary>
    /// The recordings to shrink on their own, biggest first (the most space back soonest): videos Exo Capture saved that are
    /// not already shrunk or refused (the mark), not the add-on's own output, and old enough to be finished with.
    /// </summary>
    internal static List<string> Pending(IEnumerable<(string Relative, DateTime WrittenUtc, long Bytes, string? Mark)> videos, DateTime nowUtc) =>
        [.. videos
            .Where(v => v.Mark is null && nowUtc - v.WrittenUtc >= Settle
                && v.Relative.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                && !v.Relative.EndsWith(".shrink.mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Bytes)
            .Select(v => v.Relative)];

    /// <summary>Why not now, for the unattended run: a game or a recording (as <see cref="Blocked"/>), or a laptop on its battery.</summary>
    internal static string? BlockedUnattended(bool gameRunning, bool recording, bool onBattery) =>
        Blocked(gameRunning, recording) ?? (onBattery ? "On battery power. Exo makes recordings smaller while the PC is plugged in." : null);

    /// <summary>Why not now: a game is running, or a recording is being made. Null when the PC is idle enough.</summary>
    internal static string? Blocked(bool gameRunning, bool recording) =>
        gameRunning ? "A game is running. Exo makes recordings smaller when you aren't playing."
        : recording ? "A recording is being made. Try again when it ends."
        : null;

    /// <summary>A refusal that is about the moment, not the file: not remembered, so the file is asked again later.</summary>
    internal static bool Later(string? skipped) => skipped is "busy" or "recording-in-progress" or "cancelled";

    internal static string Say(string? skipped) => skipped switch
    {
        "not-smaller" => "It was already small; nothing to gain.",
        "quality-loss" => "Making it smaller would have cost quality, so it was left as it is.",
        "hdr-unsupported" => "Exo can't make this HDR recording smaller without changing how it looks.",
        "audio-passthrough" or "unsupported-tracks" => "This recording has tracks Exo can't keep as they are, so it was left alone.",
        "no-encoder" or "no-better-encoder" => "This PC's graphics card has no encoder that would make it smaller.",
        "source-unreadable" or "not-mp4" or "unsupported-video" or "not-found" => "Exo couldn't read this recording.",
        "recording-in-progress" => "This recording is still being finished. Try again in a moment.",
        "busy" => "Exo is already making a recording smaller.",
        "cancelled" => "Stopped.",
        _ => "The smaller file didn't pass Exo's check, so the recording was left as it is.",
    };

    /// <summary>
    /// Puts the smaller file where the original was, same name and date, and sends the original to the Recycle Bin. The
    /// original moves aside first and returns if anything goes wrong, so a clip is never lost between the two. Null when
    /// done; else a line for the user, with the original in place.
    /// </summary>
    internal static string? Replace(string original, string smaller, Func<string, bool> recycle)
    {
        var aside = original + ".exo-orig";
        try
        {
            var before = new FileInfo(original);
            var after = new FileInfo(smaller);
            if (!before.Exists) return "The recording is gone.";
            if (!after.Exists || after.Length == 0 || after.Length >= before.Length) return "The smaller file isn't there.";
            if (File.Exists(aside)) return "A leftover from an earlier try is in the way.";
            var created = before.CreationTimeUtc;
            var written = before.LastWriteTimeUtc;
            File.SetCreationTimeUtc(smaller, created);
            File.SetLastWriteTimeUtc(smaller, written);
            File.Move(original, aside);
            try { File.Move(smaller, original); }
            catch
            {
                File.Move(aside, original);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The recording is open in another program. Try again after closing it.";
        }
        // A bin that won't take it leaves the original beside the new file, named .exo-orig, for the user.
        if (!recycle(aside)) Helpers.AppLog.Warn("Capture shrink: the original didn't go to the Recycle Bin; it stays as .exo-orig.");
        return null;
    }
}
