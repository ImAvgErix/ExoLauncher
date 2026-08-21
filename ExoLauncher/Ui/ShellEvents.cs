using ExoLauncher.Models;

namespace ExoLauncher.Ui;

public sealed class LaunchStatusInfo
{
    private string _gameId = "";

    public string GameId
    {
        get => _gameId;
        init => _gameId = value;
    }

    public string gameId
    {
        get => _gameId;
        init => _gameId = value;
    }

    public bool Ok { get; init; }
    public string? Message { get; init; }
    public string? Phase { get; init; }
    public int? ProcessId { get; init; }
    public string? BackendStarted { get; init; }
    public bool HandoffOnly { get; init; }
    public bool NeedsDependencies { get; init; }
}

public sealed class LibraryChangedEventArgs : EventArgs
{
    public required IReadOnlyList<GameEntry> Games { get; init; }
    public required IReadOnlyList<string> Favorites { get; init; }
}

public sealed class SearchPartialEventArgs : EventArgs
{
    public required string Query { get; init; }
    public required IReadOnlyList<StoreSearchHit> Hits { get; init; }
}
