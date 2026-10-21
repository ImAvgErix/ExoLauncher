namespace ExoLauncher.Adapters;

/// <summary>Evidence for one requested update, independent of UI sampling and stale IPC heartbeats.</summary>
internal sealed class SteamUpdateObservation(SteamUpdateObservation.Snapshot initial, bool requiredAtStart)
{
    internal readonly record struct Snapshot(bool Readable, string? Flags, long? Downloaded, long? ToDownload,
        long? Staged, long? ToStage, string? BuildId, string? TargetBuildId)
    {
        internal bool Complete => Readable && SteamStateFlags.IsTransferComplete(Flags,
            Downloaded, ToDownload, Staged, ToStage, BuildId, TargetBuildId);
    }

    internal readonly record struct Decision(bool Queued, bool Finalizing, bool Completed);
    private bool _sawCurrentWork;
    private bool _sawCurrentChange;
    private int _readyStreak;

    internal Decision Observe(Snapshot current, bool liveIpc, bool liveFolder)
    {
        if (!current.Readable)
        {
            _readyStreak = 0;
            return default;
        }
        // A failed ACF read is not a transition, and state flags alone are not
        // evidence that the requested build or its patch counters changed.
        _sawCurrentChange |= !initial.Complete && current.Complete ||
            current.BuildId is not null && current.BuildId != initial.BuildId ||
            Changed(current.Downloaded, initial.Downloaded) || Changed(current.ToDownload, initial.ToDownload) ||
            Changed(current.Staged, initial.Staged) || Changed(current.ToStage, initial.ToStage);
        var queued = SteamStateFlags.IsQueuedForTargetedPromotion(current.Flags,
            current.ToDownload, current.Downloaded, current.BuildId, current.TargetBuildId) &&
            !(SteamStateFlags.TryParse(current.Flags, out var flags) &&
              (flags & (SteamStateFlags.UpdateRunning | SteamStateFlags.Downloading | SteamStateFlags.Staging |
                        SteamStateFlags.Validating | SteamStateFlags.Preallocating | SteamStateFlags.Committing)) != 0);
        _sawCurrentWork |= !current.Complete && !queued &&
            (requiredAtStart || current != initial) && SteamStateFlags.IsBusy(current.Flags, current.ToDownload, current.Downloaded);
        var evidence = _sawCurrentWork || _sawCurrentChange || !requiredAtStart && !liveFolder;
        var finalizing = current.Complete && !liveIpc && evidence;
        _readyStreak = finalizing ? _readyStreak + 1 : 0;
        return new(queued, finalizing, finalizing && _readyStreak >= 15);
    }

    private static bool Changed(long? current, long? initial) => current is not null && initial is not null && current != initial;
}
