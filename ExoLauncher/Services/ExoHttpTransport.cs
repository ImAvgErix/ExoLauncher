using System.Security.Cryptography;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>Bounded JSON body reads with a caller deadline. The caller keeps its deadline alive through this read.</summary>
internal static class ExoHttpTransport
{
    internal static CancellationTokenSource Deadline(HttpClient client, CancellationToken caller)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (client.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);
        return deadline;
    }

    internal static async Task<JsonDocument?> ReadJsonAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes) return null;
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var sink = new MemoryStream(declared is > 0 ? (int)declared.Value : 0);
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > maxBytes - sink.Length) return null;
                sink.Write(buffer, 0, read);
            }
            if (sink.Length == 0) return null;
            sink.Position = 0;
            try { return JsonDocument.Parse(sink); }
            catch (JsonException) { return null; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (sink.TryGetBuffer(out var segment)) CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }
}
