namespace ExoLauncher.Adapters;

/// <summary>
/// Test-only stub so LibraryService / StoreSearchService compile without the full SteamAdapter graph.
/// </summary>
public sealed class SteamAdapter
{
    public bool IsAgentPresent() => false;

    internal static bool IsNonGameSteamEntry(string appId, string name, string? installDir)
    {
        _ = appId;
        _ = name;
        _ = installDir;
        return false;
    }

    /// <summary>Tests never shut Steam down, so there is nothing to resolve.</summary>
    public static string? TryResolveSteamExePublic() => null;
}
