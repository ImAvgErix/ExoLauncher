namespace ExoLauncher.Services;

// ExoBridgeProtocol sizes library.folderArt from FolderArtStore.MaxBytes. The real store needs WinRT imaging and the
// cover cache, which this plain console harness does not link, so only that limit stands in. Keep it equal to
// ExoLauncher/Services/FolderArtStore.cs.
internal static class FolderArtStore
{
    public const long MaxBytes = 15L * 1024 * 1024;
}
