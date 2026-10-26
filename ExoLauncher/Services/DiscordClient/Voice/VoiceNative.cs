using System.Runtime.InteropServices;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Discord's libdave (dave.dll, MIT, built by voice\build.ps1) through its C API (cpp/includes/dave/dave.h):
/// the MLS session that agrees the call's keys, and the per-sender frame encryptor and decryptors.
/// Buffers it returns are freed with <see cref="Free"/>; handles with their own Destroy.
/// </summary>
internal static unsafe partial class Dave
{
    private const string Lib = "dave.dll";
    internal const int CodecOpus = 1;
    internal const int MediaAudio = 0, MediaVideo = 1;
    internal const int CodecH264 = 4, CodecH265 = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FailureCallback(nint source, nint reason, nint userData);

    [LibraryImport(Lib, EntryPoint = "daveMaxSupportedProtocolVersion")] internal static partial ushort MaxProtocolVersion();
    [LibraryImport(Lib, EntryPoint = "daveFree")] internal static partial void Free(nint ptr);

    [LibraryImport(Lib, EntryPoint = "daveSessionCreate")] internal static partial nint SessionCreate(nint context, nint authSessionId, nint callback, nint userData);
    [LibraryImport(Lib, EntryPoint = "daveSessionDestroy")] internal static partial void SessionDestroy(nint session);
    [LibraryImport(Lib, EntryPoint = "daveSessionInit", StringMarshalling = StringMarshalling.Utf8)] internal static partial void SessionInit(nint session, ushort version, ulong groupId, string selfUserId);
    [LibraryImport(Lib, EntryPoint = "daveSessionReset")] internal static partial void SessionReset(nint session);
    [LibraryImport(Lib, EntryPoint = "daveSessionSetProtocolVersion")] internal static partial void SessionSetProtocolVersion(nint session, ushort version);
    [LibraryImport(Lib, EntryPoint = "daveSessionGetProtocolVersion")] internal static partial ushort SessionGetProtocolVersion(nint session);
    [LibraryImport(Lib, EntryPoint = "daveSessionSetExternalSender")] internal static partial void SessionSetExternalSender(nint session, byte* data, nuint length);
    [LibraryImport(Lib, EntryPoint = "daveSessionProcessProposals")] internal static partial void SessionProcessProposals(nint session, byte* proposals, nuint length, nint* userIds, nuint userIdCount, out nint commitWelcome, out nuint commitWelcomeLength);
    [LibraryImport(Lib, EntryPoint = "daveSessionProcessCommit")] internal static partial nint SessionProcessCommit(nint session, byte* commit, nuint length);
    [LibraryImport(Lib, EntryPoint = "daveSessionProcessWelcome")] internal static partial nint SessionProcessWelcome(nint session, byte* welcome, nuint length, nint* userIds, nuint userIdCount);
    [LibraryImport(Lib, EntryPoint = "daveSessionGetMarshalledKeyPackage")] internal static partial void SessionGetKeyPackage(nint session, out nint keyPackage, out nuint length);
    [LibraryImport(Lib, EntryPoint = "daveSessionGetKeyRatchet", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint SessionGetKeyRatchet(nint session, string userId);
    [LibraryImport(Lib, EntryPoint = "daveKeyRatchetDestroy")] internal static partial void KeyRatchetDestroy(nint ratchet);

    [LibraryImport(Lib, EntryPoint = "daveCommitResultIsFailed")] [return: MarshalAs(UnmanagedType.U1)] internal static partial bool CommitFailed(nint result);
    [LibraryImport(Lib, EntryPoint = "daveCommitResultIsIgnored")] [return: MarshalAs(UnmanagedType.U1)] internal static partial bool CommitIgnored(nint result);
    [LibraryImport(Lib, EntryPoint = "daveCommitResultDestroy")] internal static partial void CommitDestroy(nint result);
    [LibraryImport(Lib, EntryPoint = "daveWelcomeResultDestroy")] internal static partial void WelcomeDestroy(nint result);

    [LibraryImport(Lib, EntryPoint = "daveEncryptorCreate")] internal static partial nint EncryptorCreate();
    [LibraryImport(Lib, EntryPoint = "daveEncryptorDestroy")] internal static partial void EncryptorDestroy(nint encryptor);
    [LibraryImport(Lib, EntryPoint = "daveEncryptorSetKeyRatchet")] internal static partial void EncryptorSetKeyRatchet(nint encryptor, nint ratchet);
    [LibraryImport(Lib, EntryPoint = "daveEncryptorSetPassthroughMode")] internal static partial void EncryptorSetPassthrough(nint encryptor, [MarshalAs(UnmanagedType.U1)] bool passthrough);
    [LibraryImport(Lib, EntryPoint = "daveEncryptorAssignSsrcToCodec")] internal static partial void EncryptorAssignSsrc(nint encryptor, uint ssrc, int codec);
    [LibraryImport(Lib, EntryPoint = "daveEncryptorGetMaxCiphertextByteSize")] internal static partial nuint EncryptorMaxCiphertext(nint encryptor, int mediaType, nuint frameSize);
    [LibraryImport(Lib, EntryPoint = "daveEncryptorEncrypt")] internal static partial int Encrypt(nint encryptor, int mediaType, uint ssrc, byte* frame, nuint frameLength, byte* output, nuint capacity, out nuint written);

    [LibraryImport(Lib, EntryPoint = "daveDecryptorCreate")] internal static partial nint DecryptorCreate();
    [LibraryImport(Lib, EntryPoint = "daveDecryptorDestroy")] internal static partial void DecryptorDestroy(nint decryptor);
    [LibraryImport(Lib, EntryPoint = "daveDecryptorTransitionToKeyRatchet")] internal static partial void DecryptorSetKeyRatchet(nint decryptor, nint ratchet);
    [LibraryImport(Lib, EntryPoint = "daveDecryptorTransitionToPassthroughMode")] internal static partial void DecryptorSetPassthrough(nint decryptor, [MarshalAs(UnmanagedType.U1)] bool passthrough);
    [LibraryImport(Lib, EntryPoint = "daveDecryptorGetMaxPlaintextByteSize")] internal static partial nuint DecryptorMaxPlaintext(nint decryptor, int mediaType, nuint size);
    [LibraryImport(Lib, EntryPoint = "daveDecryptorDecrypt")] internal static partial int Decrypt(nint decryptor, int mediaType, byte* input, nuint length, byte* output, nuint capacity, out nuint written);

    /// <summary>Copies a buffer libdave allocated, then frees it.</summary>
    internal static byte[] Take(nint data, nuint length)
    {
        if (data == 0) return [];
        try { return new ReadOnlySpan<byte>((void*)data, checked((int)length)).ToArray(); }
        finally { Free(data); }
    }
}

/// <summary>libopus (opus.dll, BSD) at 48 kHz stereo, Discord's format: 20 ms frames of 960 samples per channel.</summary>
internal static unsafe partial class Opus
{
    private const string Lib = "opus.dll";
    internal const int SampleRate = 48000, Channels = 2, FrameSamples = 960;
    private const int ApplicationVoip = 2048, ApplicationAudio = 2049;
    private const int SetBitrate = 4002, SetComplexity = 4010, SetInbandFec = 4012, SetPacketLossPerc = 4014, SetForceChannels = 4022,
        SetSignal = 4024, SignalVoice = 3001, SignalMusic = 3002;

    [LibraryImport(Lib, EntryPoint = "opus_encoder_create")] private static partial nint EncoderCreate(int rate, int channels, int application, out int error);
    [LibraryImport(Lib, EntryPoint = "opus_encoder_destroy")] internal static partial void EncoderDestroy(nint encoder);
    [LibraryImport(Lib, EntryPoint = "opus_encoder_ctl")] private static partial int EncoderCtl(nint encoder, int request, int value);
    [LibraryImport(Lib, EntryPoint = "opus_encode_float")] internal static partial int EncodeFloat(nint encoder, float* pcm, int frameSize, byte* data, int maxBytes);
    [LibraryImport(Lib, EntryPoint = "opus_decoder_create")] private static partial nint DecoderCreate(int rate, int channels, out int error);
    [LibraryImport(Lib, EntryPoint = "opus_decoder_destroy")] internal static partial void DecoderDestroy(nint decoder);
    [LibraryImport(Lib, EntryPoint = "opus_decode_float")] internal static partial int DecodeFloat(nint decoder, byte* data, int length, float* pcm, int frameSize, int decodeFec);

    /// <summary>
    /// A voice encoder at the channel's bit rate (Discord's 64 kbps default) with in-band FEC for lost packets, at
    /// Opus's best complexity, coding one channel: the voice is mono (suppression and Voice Focus give both channels
    /// the same), so every bit goes to it, as Discord's own client sends the microphone.
    /// </summary>
    /// <summary>The share of packets expected lost (0-100), which sets how much in-band FEC each packet carries.</summary>
    internal static void SetExpectedLoss(nint encoder, int percent) => EncoderCtl(encoder, SetPacketLossPerc, Math.Clamp(percent, 0, 100));

    internal static nint NewEncoder(int bitrate = 64000)
    {
        var encoder = EncoderCreate(SampleRate, Channels, ApplicationVoip, out var error);
        if (error != 0 || encoder == 0) throw new InvalidOperationException("Opus encoder: " + error);
        // One channel of voice is transparent well under 128 kbps; more on a boosted channel only spends
        // uplink Go Live's video could use, for no audible gain.
        EncoderCtl(encoder, SetBitrate, Math.Min(bitrate, 128_000));
        EncoderCtl(encoder, SetComplexity, 10);
        EncoderCtl(encoder, SetForceChannels, 1);
        EncoderCtl(encoder, SetInbandFec, 1);
        EncoderCtl(encoder, SetPacketLossPerc, 5);
        EncoderCtl(encoder, SetSignal, SignalVoice);
        return encoder;
    }

    /// <summary>A music encoder for a stream's sound: full-band stereo at the given rate, with in-band FEC.</summary>
    internal static nint NewMusicEncoder(int bitrate = 128000)
    {
        var encoder = EncoderCreate(SampleRate, Channels, ApplicationAudio, out var error);
        if (error != 0 || encoder == 0) throw new InvalidOperationException("Opus encoder: " + error);
        EncoderCtl(encoder, SetBitrate, bitrate);
        EncoderCtl(encoder, SetComplexity, 10);
        EncoderCtl(encoder, SetInbandFec, 1);
        EncoderCtl(encoder, SetPacketLossPerc, 5);
        EncoderCtl(encoder, SetSignal, SignalMusic);
        return encoder;
    }

    internal static int Encode(nint encoder, ReadOnlySpan<float> pcm, Span<byte> data)
    {
        fixed (float* p = pcm) fixed (byte* d = data) return EncodeFloat(encoder, p, FrameSamples, d, data.Length);
    }

    internal static int Decode(nint decoder, ReadOnlySpan<byte> data, Span<float> pcm)
    {
        fixed (byte* d = data) fixed (float* p = pcm) return DecodeFloat(decoder, d, data.Length, p, FrameSamples, 0);
    }

    internal static nint NewDecoder()
    {
        var decoder = DecoderCreate(SampleRate, Channels, out var error);
        if (error != 0 || decoder == 0) throw new InvalidOperationException("Opus decoder: " + error);
        return decoder;
    }
}
