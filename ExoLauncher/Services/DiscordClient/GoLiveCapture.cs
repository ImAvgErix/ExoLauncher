using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Go Live's picture size, frame rate and bit rate: the chosen height (0 keeps the source's), never larger than
/// the source, within H.264's limits (4096 a side) and 4K's pixels, even sides. 120 fps runs up to 1440p's pixels
/// (H.264's level 5.2 carries no more); a bigger picture goes at 60. The capture also never runs faster than the
/// screen. The bit rate is H.264's usual best for the size and rate (YouTube's upload table: 8 Mbit/s at 1080p 30,
/// 12 at 1080p 60, 16 and 24 at 1440p, 40 and 60 at 4K, 1.5 times 60 fps at 120), between those points by pixels,
/// within 1 to 60 Mbit/s.
/// </summary>
internal readonly record struct GoLivePlan(int Width, int Height, int Fps, int Bitrate)
{
    // Pixels and the bit rate at 30 fps there: 720p, 1080p, 1440p, 4K.
    private static readonly (double Pixels, double Rate)[] Table = [(1280 * 720, 5e6), (1920 * 1080, 8e6), (2560 * 1440, 16e6), (3840 * 2160, 40e6)];
    private const double Most120 = 2560 * 1440;

    internal static GoLivePlan For(int sourceWidth, int sourceHeight, int height, int fps)
    {
        fps = fps >= 120 ? 120 : fps >= 60 ? 60 : fps >= 30 ? 30 : 15;
        double w = Math.Max(64, sourceWidth), h = Math.Max(64, sourceHeight);
        var scale = height > 0 ? Math.Min(1, height / h) : 1;
        scale = Math.Min(scale, Math.Min(4096 / w, 4096 / h));
        scale = Math.Min(scale, Math.Sqrt(3840.0 * 2160 / (w * h)));
        var width = Math.Max(64, (int)(w * scale) & ~1);
        var outHeight = Math.Max(64, (int)(h * scale) & ~1);
        if (fps == 120 && width * (double)outHeight > Most120 * 1.01) fps = 60;
        var rate = At30(width * (double)outHeight) * fps switch { 120 => 2.25, 60 => 1.5, 30 => 1, _ => 0.6 };
        return new(width, outHeight, fps, (int)Math.Clamp(rate, 1_000_000, 60_000_000));
    }

    // The 30 fps rate for this many pixels: along the table (log-log between its points), in proportion below 720p.
    private static double At30(double pixels)
    {
        if (pixels <= Table[0].Pixels) return Table[0].Rate * pixels / Table[0].Pixels;
        for (var i = 1; i < Table.Length; i++)
            if (pixels <= Table[i].Pixels)
            {
                var t = Math.Log(pixels / Table[i - 1].Pixels) / Math.Log(Table[i].Pixels / Table[i - 1].Pixels);
                return Table[i - 1].Rate * Math.Pow(Table[i].Rate / Table[i - 1].Rate, t);
            }
        return Table[^1].Rate;
    }
}


/// <summary>
/// Go Live's picture, from Exo Capture (<see cref="CaptureAddon"/>, its own process; Exo itself never
/// captures the screen). Exo makes an anonymous pipe, duplicates its write end into the add-on and asks
/// it to stream a window or screen (Windows.Graphics.Capture, the graphics card's H.264 encoder at a
/// constant bit rate). Encoded frames come back on the pipe and go straight to the stream's voice
/// connection, never through the page. On the pipe each frame is a 16-byte header (u32 length,
/// u32 flags with 1 for a key frame, i64 capture time in 100 ns, little-endian) and the frame (Annex B).
/// </summary>
internal sealed class GoLiveCapture : IAsyncDisposable
{
    internal const int HeaderSize = 16;
    internal const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly CaptureAddon _addon;
    private readonly AnonymousPipeServerStream _pipe;
    private readonly Lock _threadGate = new();
    private nint _readerThread;
    private long _id;
    private int _disposed, _ended;
    private long _lastKeyframe;

    /// <summary>What the add-on encodes: picture size, frame rate, bits per second.</summary>
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal int Fps { get; private set; }
    internal int Bitrate { get; private set; }

    /// <summary>The frames are HEVC (H.265), not H.264.</summary>
    internal bool Hevc { get; private set; }

    /// <summary>This PC's graphics card encodes HEVC, so the stream can be H.265 when Discord's server picks it.</summary>
    internal bool HevcSupported { get; private set; }

    /// <summary>Being replaced by a capture in another codec: its end doesn't end the stream.</summary>
    internal bool Retiring { get; set; }

    /// <summary>One encoded frame (Annex B) and its RTP timestamp (90 kHz).</summary>
    internal event Action<byte[], uint>? Frame;

    /// <summary>Each second: frames per second and bits per second really sent.</summary>
    internal event Action<int, int>? Stats;

    /// <summary>The picture stopped on its own (the window closed, the add-on stopped), and why.</summary>
    internal event Action<string>? Ended;

    private GoLiveCapture(CaptureAddon addon, AnonymousPipeServerStream pipe)
    {
        _addon = addon;
        _pipe = pipe;
    }

    /// <summary>Starts the add-on's stream of a source; the capture, or why not.</summary>
    internal static async Task<(GoLiveCapture? Capture, string? Error)> StartAsync(CaptureAddon addon, GoLiveSources.Source source, GoLivePlan plan, CancellationToken ct, bool hevc = false)
    {
        var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None, 1 << 20);
        nint remote;
        try { remote = addon.ShareHandle(pipe.ClientSafePipeHandle); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AppLog.Warn("Discord video: Exo Capture can't take the stream: " + ex.Message);
            pipe.Dispose();
            return (null, "Exo Capture isn't running, so Exo can't share your screen.");
        }
        pipe.DisposeLocalCopyOfClientHandle();
        var capture = new GoLiveCapture(addon, pipe);
        addon.Event += capture.OnAddon;
        new Thread(capture.Read) { IsBackground = true, Name = "Exo Go Live frames" }.Start();
        try
        {
            var reply = await addon.CallAsync("stream.start", new JsonObject
            {
                ["window"] = (long)source.Window,
                ["monitor"] = (long)source.Monitor,
                ["width"] = plan.Width,
                ["height"] = plan.Height,
                ["fps"] = plan.Fps,
                ["bitrate"] = plan.Bitrate,
                ["codec"] = hevc ? "hevc" : "h264",
                ["cursor"] = true,
                ["pipe"] = (long)remote,
            }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            int Read(string name, int fallback) => reply?[name] is JsonValue v && v.TryGetValue<double>(out var n) ? (int)n : fallback;
            capture._id = Read("id", 0);
            capture.Width = Read("width", plan.Width);
            capture.Height = Read("height", plan.Height);
            capture.Fps = Read("fps", plan.Fps);
            capture.Bitrate = Read("bitrate", plan.Bitrate);
            capture.Hevc = reply?["codec"] is JsonValue c && c.TryGetValue<string>(out var codec) && codec == "hevc";
            capture.HevcSupported = reply?["hevc"] is JsonValue h && h.TryGetValue<bool>(out var hevcOk) && hevcOk;
            return (capture, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            AppLog.Info("Discord video: Go Live capture did not start: " + ex.Message);
            await capture.DisposeAsync().ConfigureAwait(false);
            return (null, Explain(ex));
        }
    }

    /// <summary>
    /// The add-on's own sentences say what to do; a raw Windows error doesn't, so its step ("capture the window",
    /// "encoder output type" and so on, before the code) says which part failed. Only the capture itself is Windows'.
    /// </summary>
    internal static string Explain(Exception ex) => ex switch
    {
        InvalidOperationException { Message: "Unknown request." } => "Exo Capture needs updating to share your screen.",
        InvalidOperationException e when e.Message.StartsWith("capture the window", StringComparison.Ordinal) =>
            "Windows didn't let Exo capture that window. Share the screen it's on instead.",
        InvalidOperationException e when e.Message.StartsWith("capture the screen", StringComparison.Ordinal) =>
            "Windows didn't let Exo capture that screen. Try another one.",
        InvalidOperationException e when e.Message.StartsWith("no hardware video encoder", StringComparison.Ordinal) =>
            "Your graphics card has no video encoder Exo can use, and sharing your screen needs one.",
        InvalidOperationException e when e.Message.Contains("encoder", StringComparison.Ordinal) =>
            "Your graphics card's video encoder didn't start, and neither did Windows' own. Try a smaller resolution or frame rate.",
        InvalidOperationException e when !e.Message.Contains("(0x", StringComparison.Ordinal) => e.Message,
        TimeoutException or OperationCanceledException => "Exo Capture didn't start the stream in time.",
        _ => "Exo Capture couldn't use your graphics card to share your screen.",
    };

    /// <summary>A picture of each source, at most width across, as JPEG data URLs by id (none for a source with no picture).</summary>
    internal static async Task<Dictionary<string, string>> PicturesAsync(CaptureAddon addon, IReadOnlyList<GoLiveSources.Source> sources, int width, CancellationToken ct)
    {
        var items = new JsonArray(sources.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["window"] = (long)s.Window, ["monitor"] = (long)s.Monitor }).ToArray());
        var reply = await addon.CallAsync("stream.thumbs", new JsonObject { ["items"] = items, ["width"] = width }, ct)
            .WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var pictures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reply is JsonObject found)
            foreach (var (id, value) in found)
                if (value is JsonValue v && v.TryGetValue<string>(out var data) && data.Length > 0) pictures[id] = "data:image/jpeg;base64," + data;
        return pictures;
    }

    /// <summary>What the stream sends now, at most width across, as a JPEG data URL.</summary>
    internal async Task<string?> PreviewAsync(int width)
    {
        try
        {
            var reply = await _addon.CallAsync("stream.preview", new JsonObject { ["width"] = width }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return reply is JsonValue v && v.TryGetValue<string>(out var data) && data.Length > 0 ? "data:image/jpeg;base64," + data : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return null; }
    }

    /// <summary>A new bit rate from the next frame (Discord's server takes less, or more again).</summary>
    internal void SetBitrate(int bitrate)
    {
        Bitrate = bitrate;
        _ = _addon.CallAsync("stream.bitrate", new JsonObject { ["bitrate"] = bitrate }, CancellationToken.None)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Discord wants a key frame (someone started watching or lost one); at most four a second.</summary>
    internal void Keyframe()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastKeyframe);
        if (now - last < 250 || Interlocked.CompareExchange(ref _lastKeyframe, now, last) != last) return;
        _ = _addon.CallAsync("stream.keyframe", null, CancellationToken.None)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    internal static (int Length, bool Key, long Time) ParseHeader(ReadOnlySpan<byte> header) =>
        ((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header), int.MaxValue),
         (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) & 1) != 0,
         BinaryPrimitives.ReadInt64LittleEndian(header[8..]));

    /// <summary>A capture time (100 ns ticks) on RTP's 90 kHz video clock.</summary>
    internal static uint RtpTime(long ticks) => unchecked((uint)(ticks * 9 / 1000));

    private void Read()
    {
        lock (_threadGate) _readerThread = OpenThread(0x0001 /* THREAD_TERMINATE, which CancelSynchronousIo needs */, false, GetCurrentThreadId());
        string why;
        try
        {
            why = ReadFrames(_pipe, (frame, timestamp) => Frame?.Invoke(frame, timestamp), (fps, bits) => Stats?.Invoke(fps, bits),
                () => Volatile.Read(ref _disposed) != 0);
        }
        finally
        {
            lock (_threadGate)
            {
                if (_readerThread != 0) CloseHandle(_readerThread);
                _readerThread = 0;
            }
        }
        End(why);
    }

    /// <summary>
    /// Reads frames off the pipe until it ends, a frame is broken or <paramref name="stopped"/>; each
    /// second it reports frames per second and bits per second. Answers why it stopped.
    /// </summary>
    internal static string ReadFrames(Stream pipe, Action<byte[], uint> frame, Action<int, int> stats, Func<bool> stopped)
    {
        var header = new byte[HeaderSize];
        var clock = Stopwatch.StartNew();
        var (frames, bytes) = (0, 0L);
        try
        {
            while (!stopped())
            {
                pipe.ReadExactly(header);
                var (length, _, time) = ParseHeader(header);
                if (length > MaxFrameBytes) return "Exo Capture sent a broken frame";
                var data = new byte[length];
                pipe.ReadExactly(data);
                try { frame(data, RtpTime(time)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Discord video: a Go Live frame failed: " + ex.Message); }
                frames++;
                bytes += length;
                if (clock.ElapsedMilliseconds < 1000) continue;
                var ms = clock.Elapsed.TotalMilliseconds;
                stats((int)Math.Round(frames * 1000 / ms), (int)(bytes * 8000 / ms));
                (frames, bytes) = (0, 0);
                clock.Restart();
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException) { }
        return "Exo Capture stopped sending the picture";
    }

    private void OnAddon(string name, JsonNode? data)
    {
        if (name == "stream.ended") End(data?["reason"]?.GetValue<string>() is { Length: > 0 } reason ? reason : "the picture stopped");
        else if (name == "exited") End("Exo Capture stopped");
    }

    private void End(string why)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _ended, 1) != 0) return;
        Ended?.Invoke(why);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _addon.Event -= OnAddon;
        try
        {
            await _addon.CallAsync("stream.stop", new JsonObject { ["id"] = _id }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        // The add-on closing its end ends the read; if it didn't, the read is cancelled.
        lock (_threadGate) if (_readerThread != 0) CancelSynchronousIo(_readerThread);
        _pipe.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern nint OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool CancelSynchronousIo(nint thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
