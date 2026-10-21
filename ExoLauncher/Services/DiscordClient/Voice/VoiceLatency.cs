using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The microphone's send path measured packet by packet, for app.log's minute line: how old the voice in each packet
/// is as it leaves the socket, counted from when its oldest sound reached the microphone (the frame's first sample,
/// less what noise suppression and Voice Focus hold back, so the frame before's sound a packet carries counts too),
/// and where that time went. Times are on the performance counter in 100 ns units, as WASAPI stamps captured sound.
/// </summary>
internal sealed class VoiceLatency
{
    private long _packets, _age, _ageMax, _capture, _held, _queued, _chain, _send;

    /// <summary>When the oldest sound in a frame reached the microphone: its first sample, less what the chain held back.</summary>
    internal static long AudioAt(long frameAt, int heldSamples) => frameAt - heldSamples * 10_000_000L / Opus.SampleRate;

    internal long Packets => _packets;

    /// <summary>
    /// One packet: when its frame's first sample was captured (<paramref name="frameAt"/>) and the frame was whole,
    /// when the voice thread took it up, when the chain was done with it, and when the packet left the socket; and how
    /// many samples the chain held it back (<see cref="VoiceChain.DelaySamples"/>).
    /// </summary>
    internal void Add(long frameAt, long wholeAt, long startedAt, long processedAt, long sentAt, int heldSamples)
    {
        var audioAt = AudioAt(frameAt, heldSamples);
        var age = sentAt - audioAt;
        _packets++;
        _age += age;
        _ageMax = Math.Max(_ageMax, age);
        _capture += wholeAt - frameAt;
        _held += frameAt - audioAt;
        _queued += startedAt - wholeAt;
        _chain += processedAt - startedAt;
        _send += sentAt - processedAt;
    }

    /// <summary>The averages since the last <see cref="Reset"/>, in ms: the age at the socket and its parts, which add up to it.</summary>
    internal (double Age, double Max, double Capture, double Held, double Queued, double Chain, double Send) Averages()
    {
        double Ms(long total) => _packets == 0 ? 0 : total / (double)_packets / 10_000;
        return (Ms(_age), _ageMax / 10_000.0, Ms(_capture), Ms(_held), Ms(_queued), Ms(_chain), Ms(_send));
    }

    public override string ToString()
    {
        var a = Averages();
        return string.Create(CultureInfo.InvariantCulture,
            $"voice {a.Age:0.0} ms old at the socket on average ({a.Max:0.0} max): {a.Capture:0.0} ms capturing the frame, {a.Held:0.0} ms held back by noise suppression and Voice Focus, {a.Queued:0.0} ms waiting for the voice thread, {a.Chain:0.0} ms in the chain, {a.Send:0.0} ms through Opus, DAVE and the socket");
    }

    internal void Reset() => _packets = _age = _ageMax = _capture = _held = _queued = _chain = _send = 0;
}
