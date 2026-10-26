namespace ExoLauncher.Helpers;

/// <summary>
/// A download that stops coming. HttpClient's timeout ends at the response headers (the body is read as a stream), so a server
/// that goes quiet mid-file would hold a driver update at "downloading" for good. Each read gets its own limit instead.
/// </summary>
internal static class StallGuard
{
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>One read; a <see cref="TimeoutException"/> when nothing arrives within <paramref name="limit"/>, the caller's own cancel as itself.</summary>
    internal static async Task<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken ct = default, TimeSpan? limit = null)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(limit ?? Limit);
        try { return await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The download stopped coming."); }
    }
}
