using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The takes the voice pages make: a stereo frame mixed to mono, a mono take wrapped as a data-URL WAV the page's
/// audio element plays, and <see cref="SentSound"/>, a frame through Opus as the call encodes it and back.
/// </summary>
internal static class VoiceTake
{
    private const int Rate = Opus.SampleRate, Frame = Opus.FrameSamples;

    internal static void Mono(float[] stereo, float[] into, int at)
    {
        for (var i = 0; i < Frame; i++) into[at + i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
    }

    // 16-bit mono PCM at 48 kHz, as a data URL the page's audio element plays.
    internal static string Wav(float[] take)
    {
        var data = take.Length * 2;
        var bytes = new byte[44 + data];
        using (var w = new BinaryWriter(new MemoryStream(bytes)))
        {
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
            foreach (var x in take) w.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767f));
        }
        return "data:audio/wav;base64," + Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// What the other person hears of a frame the call sends: Opus as the call's own encoder codes it (<see cref="Opus.NewEncoder"/>,
/// at Discord's 64 kbps default) and decoded again. Voice Tune's "after" goes through it.
/// </summary>
internal sealed class SentSound : IDisposable
{
    private nint _encoder = Opus.NewEncoder(), _decoder = Opus.NewDecoder();
    private readonly float[] _decoded = new float[VoiceAudio.FrameFloats];

    /// <summary>A sent 20 ms stereo frame, in place, as the listener's decoder gives it back.</summary>
    internal void Through(float[] frame)
    {
        Span<byte> opus = stackalloc byte[1275];
        var length = Opus.Encode(_encoder, frame, opus);
        if (length > 0 && Opus.Decode(_decoder, opus[..length], _decoded) > 0) _decoded.CopyTo(frame, 0);
    }

    public void Dispose()
    {
        if (_encoder != 0) Opus.EncoderDestroy(_encoder);
        if (_decoder != 0) Opus.DecoderDestroy(_decoder);
        _encoder = _decoder = 0;
    }
}
