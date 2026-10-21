using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>Bounded, restartable transport. Callers still verify the completed payload before execution.</summary>
internal static class ResumableDownload
{
    internal const long MaxInstallerBytes = 512L * 1024 * 1024;
    private const string Prefix = "ExoLauncher-Download-";
    private sealed record State(int Schema, string Source, long? Expected, string? Sha256, string ETag, long Total);

    internal static async Task DownloadAsync(
        Uri source, string destination, string cacheDirectory,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        long? expectedSize, string? sha256, Action<long, long?>? progress, CancellationToken cancellationToken,
        long maximumBytes = MaxInstallerBytes, TimeSpan? idleTimeout = null)
    {
        if (source.Scheme != Uri.UriSchemeHttps || expectedSize is <= 0 || expectedSize > maximumBytes)
            throw new InvalidDataException("Installer metadata has an invalid URL or size.");
        // No whole-transfer deadline: a slow line must still finish. A dead
        // one is cut by the idle timeout, and a partial file resumes.
        var ct = cancellationToken;
        var idle = idleTimeout ?? TimeSpan.FromSeconds(30);
        Directory.CreateDirectory(cacheDirectory);
        RejectReparse(cacheDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            source.AbsoluteUri + "\n" + expectedSize + "\n" + sha256))).ToLowerInvariant();
        var basename = Path.Combine(cacheDirectory, Prefix + key);
        var partial = basename + ".partial";
        var metadata = basename + ".json";
        using var ownership = await AcquireAsync(basename + ".lock", ct).ConfigureAwait(false);
        Cleanup(cacheDirectory, basename);
        RejectReparse(partial);
        RejectReparse(metadata);
        State? state = ReadState(metadata, source, expectedSize, sha256, maximumBytes);
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (state is null || offset <= 0 || offset >= state.Total)
        {
            Delete(partial);
            Delete(metadata);
            state = null;
            offset = 0;
        }

        var retain = state is not null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (state is not null)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(state.ETag));
            }
            using var response = await WithIdleAsync(token => send(request, token), idle, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Installer transfer unexpectedly used content encoding.");
            var receivedTag = response.Headers.ETag;
            long? total;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (state is null || receivedTag is null || receivedTag.IsWeak || receivedTag.ToString() != state.ETag ||
                    range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
                    range.From != offset || range.Length != state.Total || range.To != state.Total - 1 ||
                    response.Content.Headers.ContentLength is { } remaining && remaining != state.Total - offset)
                    throw new InvalidDataException("The resumed installer range did not match its saved download.");
                total = state.Total;
            }
            else
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new InvalidDataException("The saved installer range is no longer available. Retry to start a fresh download.");
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("Installer transfer did not return a complete response.");
                // If-Range may legitimately return a new complete representation.
                offset = 0;
                state = null;
                retain = false;
                total = response.Content.Headers.ContentLength ?? expectedSize;
            }
            if (total is <= 0 || total > maximumBytes || expectedSize is { } expected && total is { } actual && expected != actual)
                throw new InvalidDataException("Installer download size did not match its metadata or limit.");

            await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                FileAccess.Write, FileShare.None, 65536, useAsync: true);
            output.Position = offset;
            if (receivedTag is { IsWeak: false, Tag: not "*" } && total is > 0)
            {
                state = new State(1, source.AbsoluteUri, expectedSize, sha256, receivedTag.ToString(), total.Value);
                File.WriteAllText(metadata, JsonSerializer.Serialize(state));
                retain = true;
            }
            else Delete(metadata);

            await using var input = await WithIdleAsync(token => response.Content.ReadAsStreamAsync(token), idle, ct).ConfigureAwait(false);
            var buffer = new byte[65536];
            long written = offset;
            var lastProgress = Environment.TickCount64 - 250;
            progress?.Invoke(written, total);
            while (true)
            {
                var read = await WithIdleAsync(token => input.ReadAsync(buffer.AsMemory(), token).AsTask(), idle, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (written > maximumBytes - read || total is { } length && written > length - read)
                    throw new InvalidDataException("Installer download exceeded its declared size or limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (Environment.TickCount64 - lastProgress >= 250)
                {
                    progress?.Invoke(written, total);
                    lastProgress = Environment.TickCount64;
                }
            }
            if (written == 0 || total is { } finalSize && written != finalSize)
                throw new EndOfStreamException("Installer download was interrupted. Retry to continue the download.");
            await output.FlushAsync(ct).ConfigureAwait(false);
            progress?.Invoke(written, total ?? written);
            ct.ThrowIfCancellationRequested();
            await output.DisposeAsync().ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            RejectReparse(destination);
            File.Move(partial, destination); // Never replace an existing payload.
            retain = false;
        }
        catch (InvalidDataException)
        {
            retain = false;
            throw;
        }
        finally
        {
            if (!retain || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                Delete(partial);
                Delete(metadata);
            }
        }
    }

    private static async Task<T> WithIdleAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(timeout);
        try { return await operation(idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Installer download stopped responding. Retry to continue the download."); }
    }

    private static State? ReadState(string path, Uri source, long? expected, string? sha256, long maximum)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096 || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                return null;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            return state is { Schema: 1, Total: > 0 } && state.Source == source.AbsoluteUri &&
                state.Expected == expected && state.Sha256 == sha256 && state.Total <= maximum &&
                (expected is null || state.Total == expected) &&
                EntityTagHeaderValue.TryParse(state.ETag, out var tag) && !tag.IsWeak && tag.Tag != "*" ? state : null;
        }
        catch { return null; }
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RejectReparse(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
    }

    private static void Cleanup(string directory, string current)
    {
        try
        {
            var entries = Directory.EnumerateFiles(directory, Prefix + "*.partial").Take(128)
                .Where(path => IsOwned(path)).Select(path => new FileInfo(path)).OrderByDescending(info => info.LastWriteTimeUtc).ToArray();
            var retained = 0;
            foreach (var entry in entries)
            {
                var basename = entry.FullName[..^".partial".Length];
                if (basename.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7) && retained++ < 2) continue;
                try
                {
                    RejectReparse(basename + ".lock");
                    using var ownership = new FileStream(basename + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.DeleteOnClose);
                    RejectReparse(entry.FullName);
                    RejectReparse(basename + ".json");
                    Delete(entry.FullName);
                    Delete(basename + ".json");
                }
                catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsOwned(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == Prefix.Length + 64 + 8 && name.StartsWith(Prefix, StringComparison.Ordinal) &&
            name.EndsWith(".partial", StringComparison.Ordinal) && name.AsSpan(Prefix.Length, 64).ContainsAnyExcept("0123456789abcdef") == false;
    }

    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installer staging cannot use a linked path.");
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
