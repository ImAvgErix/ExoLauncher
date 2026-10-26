// Exo Capture - self-test of Shrink: builds recordings the way the recorder does, shrinks them, and checks every promise
// Shrink makes with code that does not share Shrink's own. GPL-3.0-or-later.
//
//   ExoCaptureShrinkTest.exe [--dir <folder>] [--keep] [--real <recording.mp4>] [--no-hdr] [--quick]
//
// Built by `pwsh -File capture\build.ps1 -Test`, which runs it. Exit code 0 when every check passed. It writes only
// inside its folder (default: a folder in %TEMP%), which it empties again unless --keep. --real shrinks a copy of
// a recording made by Exo Capture itself; the recording is not touched.
#include "shrink.h"
#include "mux.h"

#include <propvarutil.h>
#include <shlwapi.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>

namespace
{
constexpr int64_t Second = 10'000'000;
int g_failed = 0, g_checks = 0;

void Expect(bool ok, const std::string& what)
{
    ++g_checks;
    if (!ok) ++g_failed;
    printf("  %s  %s\n", ok ? "PASS" : "FAIL", what.c_str());
    fflush(stdout);
}

std::string Fmt(const char* format, ...)
{
    char text[512];
    va_list args;
    va_start(args, format);
    vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    return text;
}

uint64_t SizeOf(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA info{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info)) return 0;
    return static_cast<uint64_t>(info.nFileSizeHigh) << 32 | info.nFileSizeLow;
}
bool Exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

/// FNV-1a of a whole file, and its last write time: the proof the original was not touched.
struct FileStamp
{
    uint64_t hash = 0, size = 0, written = 0;
    bool operator==(const FileStamp&) const = default;
};
FileStamp Stamp(const std::wstring& path)
{
    FileStamp s;
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (f == INVALID_HANDLE_VALUE) return s;
    uint64_t h = 14695981039346656037ull;
    std::vector<uint8_t> buffer(1 << 20);
    DWORD got = 0;
    while (ReadFile(f, buffer.data(), static_cast<DWORD>(buffer.size()), &got, nullptr) && got)
    {
        s.size += got;
        for (DWORD i = 0; i < got; ++i) { h ^= buffer[i]; h *= 1099511628211ull; }
    }
    FILETIME t{};
    GetFileTime(f, nullptr, nullptr, &t);
    s.written = static_cast<uint64_t>(t.dwHighDateTime) << 32 | t.dwLowDateTime;
    CloseHandle(f);
    s.hash = h;
    return s;
}

// ---------------------------------------------------------------- synthetic content

/// A picture with what a game or desktop has: flat areas, a smooth moving gradient, scrolling text-like detail, a fine
/// static texture, a small band of noise, and a sharp-edged colored box crossing it all.
class Content
{
public:
    Content(UINT w, UINT h) : m_w(w), m_h(h)
    {
        const int rows = static_cast<int>(h);
        m_r = { 0, rows / 6 & ~1, rows / 3 & ~1, rows / 2 & ~1, rows * 2 / 3 & ~1, rows * 5 / 6 & ~1, rows };
        // Text: 12 x 20 cells of random dot-matrix glyphs, 2 x 2 dots, words every few cells.
        m_textH = static_cast<UINT>(2 * (m_r[4] - m_r[2]));
        m_text.assign(static_cast<size_t>(w) * m_textH, 225);
        uint32_t seed = 12345;
        auto next = [&] { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed; };
        for (UINT cy = 0; cy + 20 <= m_textH; cy += 20)
            for (UINT cx = 0; cx + 12 <= w; cx += 12)
            {
                if ((cx / 12) % 7 == 6) continue;
                uint64_t mask = (static_cast<uint64_t>(next()) << 32 | next()) & 0x7FFFFFFFFull;
                for (int dy = 0; dy < 7; ++dy)
                    for (int dx = 0; dx < 5; ++dx)
                        if ((mask >> (dy * 5 + dx)) & 1)
                            for (int py = 0; py < 2; ++py)
                                for (int px = 0; px < 2; ++px) m_text[static_cast<size_t>(cy + 2 + dy * 2 + py) * w + cx + 1 + dx * 2 + px] = 30;
            }
        // Fine texture: a zone plate (detail up to the pixel grid), wide enough to slide a pixel a frame.
        m_plateW = w + 1200;
        const UINT plateH = static_cast<UINT>(m_r[5] - m_r[4]);
        m_plate.resize(static_cast<size_t>(m_plateW) * plateH);
        for (UINT y = 0; y < plateH; ++y)
            for (UINT x = 0; x < m_plateW; ++x)
            {
                double dx = static_cast<double>(x) - m_plateW / 2.0, dy = static_cast<double>(y) - plateH / 2.0;
                m_plate[static_cast<size_t>(y) * m_plateW + x] = static_cast<uint8_t>(128 + 90 * std::sin(0.0016 * (dx * dx + dy * dy)));
            }
    }

    /// One 8-bit NV12 frame (w x h luma, then interleaved UV).
    void Frame(int index, uint8_t* nv12) const
    {
        const UINT w = m_w, h = m_h;
        uint8_t* luma = nv12;
        uint8_t* chroma = nv12 + static_cast<size_t>(w) * h;
        auto row = [&](int y) { return luma + static_cast<size_t>(y) * w; };
        auto crow = [&](int y) { return chroma + static_cast<size_t>(y / 2) * w; };
        auto setChroma = [&](int y, UINT x, uint8_t u, uint8_t v) {
            if ((y & 1) || (x & 1)) return;
            crow(y)[x] = u;
            crow(y)[x + 1] = v;
        };
        // Flat bands.
        static const uint8_t flatY[4] = { 40, 90, 150, 210 }, flatU[4] = { 128, 100, 170, 128 }, flatV[4] = { 128, 170, 100, 128 };
        for (int y = m_r[0]; y < m_r[1]; ++y)
            for (UINT x = 0; x < w; ++x)
            {
                int k = static_cast<int>(x * 4 / w);
                row(y)[x] = flatY[k];
                setChroma(y, x, flatU[k], flatV[k]);
            }
        // Gradient sliding sideways.
        for (int y = m_r[1]; y < m_r[2]; ++y)
            for (UINT x = 0; x < w; ++x)
            {
                double phase = 2 * 3.14159265 * (3.0 * x / w + index * 0.004);
                row(y)[x] = static_cast<uint8_t>(110 + 90 * std::sin(phase) + (y - m_r[1]) * 0.05);
                setChroma(y, x, static_cast<uint8_t>(128 + 40 * std::sin(phase * 0.5)), static_cast<uint8_t>(128 - 40 * std::sin(phase * 0.5)));
            }
        // Text scrolling up, three rows a frame.
        const int scroll = (index * 3) % static_cast<int>(m_textH);
        for (int y = m_r[2]; y < m_r[4]; ++y)
        {
            const uint8_t* src = m_text.data() + static_cast<size_t>((y - m_r[2] + scroll) % m_textH) * w;
            memcpy(row(y), src, w);
            for (UINT x = 0; x < w; x += 2) setChroma(y, x, 128, 128);
        }
        // Fine texture, sliding a pixel a frame.
        for (int y = m_r[4]; y < m_r[5]; ++y)
        {
            memcpy(row(y), m_plate.data() + static_cast<size_t>(y - m_r[4]) * m_plateW + (index % 1200), w);
            for (UINT x = 0; x < w; x += 2) setChroma(y, x, static_cast<uint8_t>(128 + 20 * std::sin(x * 0.02)), 128);
        }
        // Smooth ramp, and a strip of noise.
        const UINT noiseFrom = w - w * 22 / 100;
        uint32_t seed = 2463534242u + static_cast<uint32_t>(index) * 9973u;
        for (int y = m_r[5]; y < m_r[6]; ++y)
        {
            for (UINT x = 0; x < noiseFrom; ++x) row(y)[x] = static_cast<uint8_t>(30 + 190ull * x / noiseFrom);
            for (UINT x = noiseFrom; x < w; ++x)
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                row(y)[x] = static_cast<uint8_t>(118 + seed % 21);
            }
            for (UINT x = 0; x < w; x += 2) setChroma(y, x, 128, 128);
        }
        // A sharp-edged colored box bouncing across everything.
        const int side = static_cast<int>(h / 7 & ~1u);
        auto bounce = [](int t, int range) { int m = t % (2 * range); return m < range ? m : 2 * range - m; };
        const int bx = bounce(index * 7, static_cast<int>(w) - side) & ~1, by = bounce(index * 3, static_cast<int>(h) - side) & ~1;
        for (int y = by; y < by + side; ++y)
            for (int x = bx; x < bx + side; ++x)
            {
                bool dark = (((x - bx) / 16) + ((y - by) / 16)) & 1;
                row(y)[x] = dark ? 20 : 235;
                setChroma(y, x, 200, 60);
            }
    }

private:
    UINT m_w, m_h;
    std::vector<int> m_r;
    std::vector<uint8_t> m_text;
    UINT m_textH = 0, m_plateW = 0;
    std::vector<uint8_t> m_plate;
};

/// PCM to raw AAC the way the recorder's AacEncoder does it (48 kHz stereo, 192 kbit/s).
class Aac
{
public:
    Aac()
    {
        MFT_REGISTER_TYPE_INFO in{ MFMediaType_Audio, MFAudioFormat_PCM }, out{ MFMediaType_Audio, MFAudioFormat_AAC };
        IMFActivate** list = nullptr;
        UINT32 count = 0;
        Check(MFTEnumEx(MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, &in, &out, &list, &count), "AAC encoder");
        HRESULT hr = count ? list[0]->ActivateObject(IID_PPV_ARGS(m_mft.put())) : MF_E_TOPO_CODEC_NOT_FOUND;
        for (UINT32 i = 0; i < count; ++i) list[i]->Release();
        CoTaskMemFree(list);
        Check(hr, "AAC encoder");
        com_ptr<IMFMediaType> pcm, aac;
        Check(MFCreateMediaType(pcm.put()), "media type");
        pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        pcm->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
        pcm->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
        pcm->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
        pcm->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 48000 * 4);
        Check(MFCreateMediaType(aac.put()), "media type");
        aac->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        aac->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        aac->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        aac->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
        aac->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
        aac->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
        aac->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
        aac->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        Check(m_mft->SetOutputType(0, aac.get(), 0), "AAC output");
        Check(m_mft->SetInputType(0, pcm.get(), 0), "AAC input");
        Check(m_mft->GetOutputCurrentType(0, m_type.put()), "AAC type");
        m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }
    com_ptr<IMFMediaType> Type() const { return m_type; }

    /// A tone (frequency, with a slow tremolo so the two tracks differ) for this many seconds, as packets of track `track`.
    std::vector<PacketPtr> Tone(int track, double hz, double seconds)
    {
        std::vector<PacketPtr> out;
        const int total = static_cast<int>(seconds * 48000);
        std::vector<int16_t> pcm(1024 * 2);
        for (int at = 0; at + 1024 <= total; at += 1024)
        {
            for (int i = 0; i < 1024; ++i)
            {
                double t = (at + i) / 48000.0;
                double level = 0.25 * (0.75 + 0.25 * std::sin(2 * 3.14159265 * 0.5 * t));
                pcm[i * 2] = static_cast<int16_t>(32767 * level * std::sin(2 * 3.14159265 * hz * t));
                pcm[i * 2 + 1] = static_cast<int16_t>(32767 * level * std::sin(2 * 3.14159265 * hz * 1.5 * t));
            }
            Encode(track, pcm.data(), 1024, static_cast<int64_t>(at) * Second / 48000, out);
        }
        return out;
    }

private:
    void Encode(int track, const int16_t* pcm, int frames, int64_t time, std::vector<PacketPtr>& out)
    {
        com_ptr<IMFMediaBuffer> buffer;
        Check(MFCreateMemoryBuffer(frames * 4, buffer.put()), "sound buffer");
        BYTE* data = nullptr;
        buffer->Lock(&data, nullptr, nullptr);
        memcpy(data, pcm, static_cast<size_t>(frames) * 4);
        buffer->Unlock();
        buffer->SetCurrentLength(frames * 4);
        com_ptr<IMFSample> sample;
        Check(MFCreateSample(sample.put()), "sound sample");
        sample->AddBuffer(buffer.get());
        sample->SetSampleTime(time);
        sample->SetSampleDuration(static_cast<int64_t>(frames) * Second / 48000);
        Check(m_mft->ProcessInput(0, sample.get(), 0), "AAC encode");
        for (;;)
        {
            com_ptr<IMFMediaBuffer> outBuffer;
            Check(MFCreateMemoryBuffer(8192, outBuffer.put()), "AAC buffer");
            com_ptr<IMFSample> outSample;
            Check(MFCreateSample(outSample.put()), "AAC sample");
            outSample->AddBuffer(outBuffer.get());
            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = outSample.get();
            DWORD status = 0;
            HRESULT hr = m_mft->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            Check(hr, "AAC output");
            auto packet = std::make_shared<Packet>();
            packet->track = track;
            packet->key = true;
            LONGLONG t = 0, d = 0;
            outSample->GetSampleTime(&t);
            outSample->GetSampleDuration(&d);
            packet->time = t;
            packet->duration = d;
            BYTE* bytes = nullptr;
            DWORD length = 0;
            outBuffer->Lock(&bytes, nullptr, &length);
            packet->data.assign(bytes, bytes + length);
            outBuffer->Unlock();
            if (!packet->data.empty()) out.push_back(packet);
        }
    }
    com_ptr<IMFTransform> m_mft;
    com_ptr<IMFMediaType> m_type;
};

struct ClipSpec
{
    UINT width = 1920, height = 1080, fps = 60;
    double seconds = 10;
    int audioTracks = 2;
    bool hdr = false;
    Codec codec = Codec::HEVC;
    UINT bitrate = 24'000'000; // 0.19 bits a pixel: Exo Capture's "best quality"
    double toneHz = 440;
};

/// A recording as Exo Capture makes one: the recorder's own VideoEncoder (hardware, fixed rate, no B-frames) and
/// Mp4Writer, AAC from the same settings.
void MakeClip(GpuDevice& gpu, const std::wstring& path, const ClipSpec& spec)
{
    std::vector<PacketPtr> packets;
    std::mutex lock;
    com_ptr<IMFMediaType> videoType;
    const int frames = static_cast<int>(spec.seconds * spec.fps);
    {
        VideoEncoder encoder(gpu, spec.codec, spec.width, spec.height, spec.fps, spec.bitrate, spec.hdr, false, [&](PacketPtr p) {
            std::lock_guard guard(lock);
            packets.push_back(p);
        });
        Content content(spec.width, spec.height);
        std::vector<uint8_t> nv12(static_cast<size_t>(spec.width) * spec.height * 3 / 2);
        std::vector<uint16_t> p010(nv12.size());
        std::atomic<int> inFlight{ 0 };
        for (int i = 0; i < frames; ++i)
        {
            content.Frame(i, nv12.data());
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = spec.width;
            desc.Height = spec.height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = spec.hdr ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA init{};
            if (spec.hdr)
            {
                // 10-bit samples sit in the high bits of 16.
                for (size_t k = 0; k < nv12.size(); ++k) p010[k] = static_cast<uint16_t>(((nv12[k] << 2) | (nv12[k] >> 6)) << 6);
                init.pSysMem = p010.data();
                init.SysMemPitch = spec.width * 2;
            }
            else
            {
                init.pSysMem = nv12.data();
                init.SysMemPitch = spec.width;
            }
            com_ptr<ID3D11Texture2D> texture;
            Check(gpu.device->CreateTexture2D(&desc, &init, texture.put()), "test frame texture");
            while (inFlight >= 2) Sleep(1);
            ++inFlight;
            encoder.Submit(texture.get(), static_cast<int64_t>(i) * Second / spec.fps, [&inFlight] { --inFlight; });
        }
        // The encoder keeps a frame or two until it is destroyed: wait for the packets, not for the textures.
        for (int waited = 0; waited < 5000; ++waited)
        {
            { std::lock_guard guard(lock); if (packets.size() >= static_cast<size_t>(frames)) break; }
            Sleep(1);
        }
        videoType = encoder.OutputType();
    }
    if (!videoType) throw Failure(E_FAIL, "the test encoder produced nothing");

    StreamFormat format;
    format.video = videoType;
    format.fps = static_cast<int>(spec.fps);
    for (int t = 0; t < spec.audioTracks; ++t)
    {
        Aac aac;
        format.audio.push_back(aac.Type());
        auto tone = aac.Tone(t + 1, spec.toneHz * (t + 1), spec.seconds);
        packets.insert(packets.end(), tone.begin(), tone.end());
    }
    std::stable_sort(packets.begin(), packets.end(), [](const PacketPtr& a, const PacketPtr& b) {
        return a->time != b->time ? a->time < b->time : a->track < b->track;
    });
    DeleteFileW(path.c_str());
    Mp4Writer writer(path, format);
    for (auto& p : packets) writer.Write(p);
    writer.Finish();
}

// ---------------------------------------------------------------- measuring a file, without Shrink's code

struct Facts
{
    double seconds = 0;
    UINT width = 0, height = 0;
    double fps = 0;
    GUID codec{};
    int audioTracks = 0;
    std::vector<uint64_t> audioHash;
    std::vector<uint64_t> audioPackets;
    uint64_t videoSamples = 0;
    int keyFrames = 0;
    double maxKeyGap = 0, firstKey = -1;
    bool bFrames = false;
    UINT32 primaries = 0, transfer = 0, matrix = 0, range = 0, profile = 0;
};

com_ptr<IMFSourceReader> Reader(const std::wstring& path, bool hardware)
{
    com_ptr<IMFAttributes> attrs;
    MFCreateAttributes(attrs.put(), 2);
    attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware);
    com_ptr<IMFSourceReader> reader;
    Check(MFCreateSourceReaderFromURL(path.c_str(), attrs.get(), reader.put()), ("open " + Narrow(path)).c_str());
    return reader;
}

/// What the file holds, read as compressed packets (no decoder): tracks, packet hashes, key frames, order.
Facts Measure(const std::wstring& path)
{
    Facts f;
    auto reader = Reader(path, false);
    std::map<DWORD, int> audioIndex;
    DWORD videoStream = ~0u;
    for (DWORD i = 0;; ++i)
    {
        com_ptr<IMFMediaType> type;
        if (reader->GetNativeMediaType(i, 0, type.put()) == MF_E_INVALIDSTREAMNUMBER) break;
        GUID major{};
        type->GetMajorType(&major);
        if (major == MFMediaType_Video)
        {
            videoStream = i;
            MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &f.width, &f.height);
            MFVideoArea area{};
            if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr)) && area.Area.cx > 0 && area.Area.cy > 0)
            {
                f.width = area.Area.cx;
                f.height = area.Area.cy;
            }
            UINT32 n = 0, d = 1;
            MFGetAttributeRatio(type.get(), MF_MT_FRAME_RATE, &n, &d);
            f.fps = d ? static_cast<double>(n) / d : 0;
            type->GetGUID(MF_MT_SUBTYPE, &f.codec);
            f.primaries = MFGetAttributeUINT32(type.get(), MF_MT_VIDEO_PRIMARIES, 0);
            f.transfer = MFGetAttributeUINT32(type.get(), MF_MT_TRANSFER_FUNCTION, 0);
            f.matrix = MFGetAttributeUINT32(type.get(), MF_MT_YUV_MATRIX, 0);
            f.range = MFGetAttributeUINT32(type.get(), MF_MT_VIDEO_NOMINAL_RANGE, 0);
            f.profile = MFGetAttributeUINT32(type.get(), MF_MT_MPEG2_PROFILE, 0);
        }
        else if (major == MFMediaType_Audio)
        {
            audioIndex[i] = f.audioTracks++;
            f.audioHash.push_back(14695981039346656037ull);
            f.audioPackets.push_back(0);
        }
        reader->SetCurrentMediaType(i, nullptr, type.get());
        reader->SetStreamSelection(i, TRUE);
    }
    PROPVARIANT value;
    PropVariantInit(&value);
    if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &value))) f.seconds = value.uhVal.QuadPart / 1e7;
    PropVariantClear(&value);
    int64_t lastKey = -1, lastTime = -1;
    for (;;)
    {
        DWORD stream = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        if (FAILED(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &stream, &flags, &time, sample.put()))) break;
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
        {
            reader->SetStreamSelection(stream, FALSE);
            bool any = false;
            for (DWORD i = 0;; ++i)
            {
                BOOL on = FALSE;
                if (FAILED(reader->GetStreamSelection(i, &on))) break;
                any |= on != FALSE;
            }
            if (!any) break;
            continue;
        }
        if (!sample) continue;
        if (stream == videoStream)
        {
            ++f.videoSamples;
            if (time < lastTime) f.bFrames = true;
            lastTime = std::max(lastTime, static_cast<int64_t>(time));
            if (MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, FALSE))
            {
                ++f.keyFrames;
                if (lastKey >= 0) f.maxKeyGap = std::max(f.maxKeyGap, (time - lastKey) / 1e7);
                if (f.firstKey < 0) f.firstKey = time / 1e7;
                lastKey = time;
            }
        }
        else if (auto it = audioIndex.find(stream); it != audioIndex.end())
        {
            com_ptr<IMFMediaBuffer> buffer;
            sample->ConvertToContiguousBuffer(buffer.put());
            BYTE* data = nullptr;
            DWORD length = 0;
            buffer->Lock(&data, nullptr, &length);
            uint64_t& h = f.audioHash[it->second];
            for (DWORD i = 0; i < length; ++i) { h ^= data[i]; h *= 1099511628211ull; }
            buffer->Unlock();
            ++f.audioPackets[it->second];
        }
    }
    return f;
}

/// Decodes the whole video on the CPU path (the reader's own, as a media player would) and keeps the luma of every
/// `every`th frame. Returns the frame count; errors is set when a read failed.
uint64_t DecodeAll(const std::wstring& path, bool tenBit, int every, std::vector<std::vector<uint16_t>>& lumas, bool& errors)
{
    errors = false;
    auto reader = Reader(path, true);
    const DWORD video = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    com_ptr<IMFMediaType> want;
    MFCreateMediaType(want.put());
    want->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    want->SetGUID(MF_MT_SUBTYPE, tenBit ? MFVideoFormat_P010 : MFVideoFormat_NV12);
    if (FAILED(reader->SetCurrentMediaType(video, nullptr, want.get()))) { errors = true; return 0; }
    com_ptr<IMFMediaType> native;
    reader->GetNativeMediaType(video, 0, native.put());
    UINT w = 0, h = 0;
    MFGetAttributeSize(native.get(), MF_MT_FRAME_SIZE, &w, &h);
    MFVideoArea area{};
    if (SUCCEEDED(native->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr)) && area.Area.cx > 0 && area.Area.cy > 0)
    {
        w = area.Area.cx;
        h = area.Area.cy;
    }
    uint64_t count = 0;
    for (;;)
    {
        DWORD stream = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        if (FAILED(reader->ReadSample(video, 0, &stream, &flags, &time, sample.put())) || (flags & MF_SOURCE_READERF_ERROR)) { errors = true; break; }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        if (!sample) continue;
        if (every > 0 && count % every == 0)
        {
            com_ptr<IMFMediaBuffer> buffer;
            sample->GetBufferByIndex(0, buffer.put());
            BYTE* src = nullptr;
            LONG pitch = 0;
            auto twoD = buffer.try_as<IMF2DBuffer>();
            if (twoD && SUCCEEDED(twoD->Lock2D(&src, &pitch)))
            {
                std::vector<uint16_t> plane(static_cast<size_t>(w) * h);
                for (UINT y = 0; y < h; ++y)
                    for (UINT x = 0; x < w; ++x)
                        plane[static_cast<size_t>(y) * w + x] = tenBit ? (reinterpret_cast<const uint16_t*>(src + static_cast<size_t>(y) * pitch)[x] >> 6) : src[static_cast<size_t>(y) * pitch + x];
                lumas.push_back(std::move(plane));
                twoD->Unlock2D();
            }
        }
        ++count;
    }
    return count;
}

// ---------------------------------------------------------------- the checks

struct Row
{
    std::string name;
    double inMB = 0, outMB = 0, ratio = 0, mean = 0, min = 0, seconds = 0;
    std::string encoder, mode, note;
};
std::vector<Row> g_rows;

std::string Mb(uint64_t bytes) { return Fmt("%.2f", bytes / 1048576.0); }

/// Runs Shrink on a recording and holds it to every promise, measured separately.
Row ShrinkAndCheck(GpuDevice& gpu, const std::string& name, const std::wstring& input, ShrinkOptions options, bool hdr, const std::string& expectCodec)
{
    printf("\n== %s\n", name.c_str());
    fflush(stdout);
    Row row;
    row.name = name;
    options.input = input;
    std::wstring output = input.substr(0, input.size() - 4) + L".shrink.mp4";
    DeleteFileW(output.c_str());
    FileStamp before = Stamp(input);
    std::vector<std::pair<int64_t, double>> reports;
    options.progress = [&](double p) { reports.emplace_back(Now100ns(), p); };
    ShrinkResult r = Shrink(options);
    row.inMB = r.inputBytes / 1048576.0;
    row.seconds = r.seconds;
    row.encoder = Narrow(r.encoder);
    row.mode = r.mode;

    Expect(Stamp(input) == before, "the original is byte-identical and keeps its write time");
    Expect(!Exists(output + L".part"), "no partial file is left");
    // Progress: at most twice a second, never backwards.
    bool gaps = true, rising = true;
    for (size_t i = 1; i < reports.size(); ++i)
    {
        if (reports[i].first - reports[i - 1].first < 4'900'000) gaps = false;
        if (reports[i].second + 1e-9 < reports[i - 1].second) rising = false;
    }
    Expect(gaps && rising && !reports.empty(), Fmt("%zu progress reports, none closer than 0.5 s, none backwards", reports.size()));

    if (!r.ok)
    {
        row.note = "skipped: " + r.skipped;
        printf("  skipped: %s (%s)\n", r.skipped.c_str(), r.detail.c_str());
        Expect(!Exists(output), "a skipped file leaves no output");
        return row;
    }
    row.outMB = r.outputBytes / 1048576.0;
    row.ratio = r.ratio;
    row.mean = r.ssimMean;
    row.min = r.ssimMin;
    Expect(Exists(output) && SizeOf(output) == r.outputBytes, "the output exists beside the input as <name>.shrink.mp4");
    Expect(r.ratio <= 1.0 - ShrinkMinSaving, Fmt("at least 15%% smaller: %s MB -> %s MB (ratio %.3f)", Mb(r.inputBytes).c_str(), Mb(r.outputBytes).c_str(), r.ratio));
    Expect(expectCodec.empty() || Narrow(r.codec) == expectCodec, "codec " + Narrow(r.codec) + (expectCodec.empty() ? "" : ", expected " + expectCodec));

    Facts a = Measure(input), b = Measure(output);
    Expect(std::abs(a.seconds - b.seconds) <= ShrinkDurationSlack, Fmt("duration %.3f s -> %.3f s", a.seconds, b.seconds));
    Expect(a.audioTracks == b.audioTracks && b.audioTracks >= 1, Fmt("%d audio tracks -> %d", a.audioTracks, b.audioTracks));
    bool sameAudio = a.audioHash == b.audioHash && a.audioPackets == b.audioPackets;
    Expect(sameAudio, "every audio packet of every track is the same bytes, in the same order (passthrough)");
    Expect(std::abs(static_cast<double>(a.videoSamples) - static_cast<double>(b.videoSamples)) <= 0.01 * a.videoSamples,
        Fmt("frames %llu -> %llu", a.videoSamples, b.videoSamples));
    Expect(a.width == b.width && a.height == b.height && std::abs(a.fps - b.fps) < 0.01, Fmt("%ux%u @ %.2f kept", b.width, b.height, b.fps));
    Expect(b.keyFrames < 2 || (b.maxKeyGap >= 1.0 && b.maxKeyGap <= 4.2), Fmt("key frames every %.2f s at most (%d of them, first at %.2f s)", b.maxKeyGap, b.keyFrames, b.firstKey));
    printf("  info  B-frames in the new file: %s; the old: %s; old key frame gap %.2f s\n", b.bFrames ? "yes" : "no", a.bFrames ? "yes" : "no", a.maxKeyGap);
    row.note = Fmt("GOP %.1fs, B-frames %s", b.maxKeyGap, b.bFrames ? "yes" : "no");
    if (hdr)
    {
        Expect(a.primaries == b.primaries && a.transfer == b.transfer && a.matrix == b.matrix && a.range == b.range && b.transfer == MFVideoTransFunc_2084,
            Fmt("HDR kept: primaries %u, transfer %u, matrix %u, range %u", b.primaries, b.transfer, b.matrix, b.range));
        Expect(a.profile == b.profile && b.profile == eAVEncH265VProfile_Main_420_10, Fmt("still 10-bit: HEVC profile %u (Main10 is 2), was %u", b.profile, a.profile));
    }
    else
        Expect(a.primaries == b.primaries && a.transfer == b.transfer && a.matrix == b.matrix && a.range == b.range,
            Fmt("color attributes kept: primaries %u, transfer %u, matrix %u, range %u", b.primaries, b.transfer, b.matrix, b.range));

    // The whole new file decodes, and the picture matches (re-measured here, 1 frame in 20).
    std::vector<std::vector<uint16_t>> la, lb;
    bool errA = false, errB = false;
    uint64_t na = DecodeAll(input, hdr, 20, la, errA), nb = DecodeAll(output, hdr, 20, lb, errB);
    Expect(!errB && nb > 0, Fmt("the whole output decodes without error (%llu frames)", nb));
    Expect(!errA && std::abs(static_cast<double>(na) - static_cast<double>(nb)) <= 0.01 * na, Fmt("decoded frame counts %llu -> %llu", na, nb));
    double sum = 0, lowest = 1;
    size_t n = std::min(la.size(), lb.size());
    for (size_t i = 0; i < n; ++i)
    {
        double s = Ssim(la[i].data(), lb[i].data(), a.width, a.height, hdr ? 1023 : 255);
        sum += s;
        lowest = std::min(lowest, s);
    }
    double mine = n ? sum / n : 0;
    Expect(n >= 6 && mine >= ShrinkSsimMean && lowest >= ShrinkSsimMin, Fmt("independent SSIM over %zu frames: mean %.4f, lowest %.4f", n, mine, lowest));
    Expect(r.ssimMean >= ShrinkSsimMean && r.ssimMin >= ShrinkSsimMin, Fmt("Shrink's own SSIM over its 24 frames: mean %.4f, lowest %.4f", r.ssimMean, r.ssimMin));
    Expect(std::abs(mine - r.ssimMean) < 0.01, Fmt("the two agree to within 0.01 (%.4f vs %.4f)", mine, r.ssimMean));
    printf("  info  encoder: %s, rate control: %s, %.1f s\n", row.encoder.c_str(), r.mode.c_str(), r.seconds);
    return row;
}

/// Cuts a recording's packets into a new MP4 with something wrong in it (the verifier must catch each).
void Remux(const std::wstring& input, const std::wstring& output, int dropAudio, int corruptAudio)
{
    auto reader = Reader(input, false);
    struct Stream { DWORD index; bool video; int out; };
    std::vector<Stream> streams;
    StreamFormat format;
    int audio = 0;
    for (DWORD i = 0;; ++i)
    {
        com_ptr<IMFMediaType> type;
        if (reader->GetNativeMediaType(i, 0, type.put()) == MF_E_INVALIDSTREAMNUMBER) break;
        GUID major{};
        type->GetMajorType(&major);
        reader->SetCurrentMediaType(i, nullptr, type.get());
        if (major == MFMediaType_Video)
        {
            format.video = type;
            UINT32 n = 60, d = 1;
            MFGetAttributeRatio(type.get(), MF_MT_FRAME_RATE, &n, &d);
            format.fps = static_cast<int>(n / std::max<UINT32>(d, 1));
            streams.push_back({ i, true, 0 });
        }
        else
        {
            if (audio != dropAudio) format.audio.push_back(type);
            streams.push_back({ i, false, audio == dropAudio ? -1 : 1 + audio - (dropAudio >= 0 && audio > dropAudio ? 1 : 0) });
            ++audio;
        }
        reader->SetStreamSelection(i, TRUE);
    }
    Mp4Writer writer(output, format);
    int open = static_cast<int>(streams.size());
    std::vector<bool> done(streams.size(), false);
    while (open > 0)
    {
        DWORD actual = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        if (FAILED(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &actual, &flags, &time, sample.put()))) break;
        auto it = std::find_if(streams.begin(), streams.end(), [&](const Stream& s) { return s.index == actual; });
        if (it == streams.end()) continue;
        size_t slot = it - streams.begin();
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { if (!done[slot]) { done[slot] = true; --open; } continue; }
        if (!sample || it->out < 0) continue;
        auto packet = std::make_shared<Packet>();
        packet->track = it->out;
        packet->time = time;
        LONGLONG d = 0;
        sample->GetSampleDuration(&d);
        packet->duration = d;
        packet->key = MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, FALSE) != FALSE;
        com_ptr<IMFMediaBuffer> buffer;
        sample->ConvertToContiguousBuffer(buffer.put());
        BYTE* data = nullptr;
        DWORD length = 0;
        buffer->Lock(&data, nullptr, &length);
        packet->data.assign(data, data + length);
        buffer->Unlock();
        if (!it->video && corruptAudio >= 0 && it->out == corruptAudio + 1 && packet->data.size() > 8 && packet->time > 3 * Second) packet->data[5] ^= 0x55;
        writer.Write(packet);
    }
    writer.Finish();
}

std::string Verdict(GpuDevice& gpu, const std::wstring& input, const std::wstring& output, std::string* detail = nullptr)
{
    auto v = VerifyShrink(gpu, input, output, nullptr);
    if (detail) *detail = v.detail;
    printf("      -> %s%s%s\n", v.reason.empty() ? "passes" : v.reason.c_str(), v.detail.empty() ? "" : ": ", v.detail.c_str());
    return v.reason;
}

void SsimUnit()
{
    printf("\n== SSIM on known pictures\n");
    const UINT w = 256, h = 128;
    std::vector<uint16_t> a(w * h), b(w * h), c(w * h), d(w * h);
    uint32_t seed = 7;
    for (UINT y = 0; y < h; ++y)
        for (UINT x = 0; x < w; ++x)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            a[y * w + x] = static_cast<uint16_t>(64 + (x * 128 / w) + (((x / 8) + (y / 8)) & 1) * 40);
            b[y * w + x] = a[y * w + x];
            c[y * w + x] = static_cast<uint16_t>(std::clamp<int>(a[y * w + x] + static_cast<int>(seed % 9) - 4, 0, 255)); // light noise
            d[y * w + x] = static_cast<uint16_t>(std::clamp<int>(a[y * w + x] / 2 + static_cast<int>(seed % 61), 0, 255));  // wrecked
        }
    double same = Ssim(a.data(), b.data(), w, h, 255), light = Ssim(a.data(), c.data(), w, h, 255), bad = Ssim(a.data(), d.data(), w, h, 255);
    Expect(same > 0.99999, Fmt("identical pictures: %.5f", same));
    Expect(light > 0.9 && light < same, Fmt("light noise: %.4f", light));
    Expect(bad < 0.5, Fmt("a wrecked picture: %.4f", bad));
    std::vector<uint16_t> flat(w * h, 100), flat2(w * h, 100);
    Expect(Ssim(flat.data(), flat2.data(), w, h, 255) > 0.9999, "two flat pictures agree (no divide by zero)");
    std::vector<uint16_t> ten(w * h), ten2(w * h);
    for (size_t i = 0; i < ten.size(); ++i) { ten[i] = static_cast<uint16_t>(a[i] * 4); ten2[i] = ten[i]; }
    Expect(Ssim(ten.data(), ten2.data(), w, h, 1023) > 0.99999, "10-bit range works");
}

/// --dump: the first packets of every track of a file (size, time, decode time), to look at what a writer did.
void Dump(const std::wstring& path)
{
    auto reader = Reader(path, false);
    for (DWORD i = 0;; ++i)
    {
        com_ptr<IMFMediaType> type;
        if (reader->GetNativeMediaType(i, 0, type.put()) == MF_E_INVALIDSTREAMNUMBER) break;
        GUID major{}, sub{};
        type->GetMajorType(&major);
        type->GetGUID(MF_MT_SUBTYPE, &sub);
        UINT w = 0, h = 0;
        MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &w, &h);
        MFVideoArea area{};
        bool hasArea = SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr));
        printf("stream %lu: %s frame %ux%u aperture %s %ldx%ld profile %u\n", i, major == MFMediaType_Video ? "video" : "audio", w, h, hasArea ? "yes" : "no", area.Area.cx, area.Area.cy,
            MFGetAttributeUINT32(type.get(), MF_MT_MPEG2_PROFILE, 0));
        reader->SetCurrentMediaType(i, nullptr, type.get());
        reader->SetStreamSelection(i, TRUE);
    }
    std::map<DWORD, int> shown;
    for (int n = 0; n < 4000; ++n)
    {
        DWORD stream = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        if (FAILED(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &stream, &flags, &time, sample.put())) || (flags & MF_SOURCE_READERF_ENDOFSTREAM)) break;
        if (!sample || shown[stream]++ >= 12) continue;
        DWORD size = 0;
        sample->GetTotalLength(&size);
        UINT64 dts = 0;
        bool hasDts = SUCCEEDED(sample->GetUINT64(MFSampleExtension_DecodeTimestamp, &dts));
        LONGLONG dur = 0;
        sample->GetSampleDuration(&dur);
        printf("  s%lu pts %lld dur %lld dts %s%lld size %lu key %u\n", stream, time, dur, hasDts ? "" : "none ", hasDts ? static_cast<long long>(dts) : 0, size,
            MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, 0));
    }
}
}

/// --codecapi: what each hardware encoder says about the settings Shrink uses.
void CodecApi(GpuDevice& gpu)
{
    struct Prop { const GUID* key; const char* name; };
    static const Prop props[] = {
        { &CODECAPI_AVEncCommonRateControlMode, "RateControlMode" }, { &CODECAPI_AVEncCommonQuality, "Quality" },
        { &CODECAPI_AVEncCommonQualityVsSpeed, "QualityVsSpeed" }, { &CODECAPI_AVEncMPVGOPSize, "GOPSize" },
        { &CODECAPI_AVEncMPVDefaultBPictureCount, "DefaultBPictureCount" }, { &CODECAPI_AVEncMPVGOPSInSeq, "GOPsInSeq" },
        { &CODECAPI_AVEncVideoMaxNumRefFrame, "MaxNumRefFrame" }, { &CODECAPI_AVEncVideoLTRBufferControl, "LTRBufferControl" },
        { &CODECAPI_AVEncCommonMultipassMode, "MultipassMode" }, { &CODECAPI_AVEncVideoMinQP, "MinQP" }, { &CODECAPI_AVEncVideoMaxQP, "MaxQP" },
        { &CODECAPI_AVEncCommonMeanBitRate, "MeanBitRate" }, { &CODECAPI_AVEncCommonMaxBitRate, "MaxBitRate" }, { &CODECAPI_AVEncCommonBufferSize, "BufferSize" },
        { &CODECAPI_AVLowLatencyMode, "LowLatencyMode" }, { &CODECAPI_AVEncVideoEncodeQP, "EncodeQP" }, { &CODECAPI_AVEncVideoContentType, "ContentType" },
        { &CODECAPI_AVEncH264CABACEnable, "H264CABAC" }, { &CODECAPI_AVEncVideoTemporalLayerCount, "TemporalLayerCount" },
    };
    for (Codec codec : { Codec::H264, Codec::HEVC, Codec::AV1 })
    {
        com_ptr<IMFActivate> activate;
        std::wstring name;
        if (!FindMft(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, MFVideoFormat_NV12, Subtype(codec), &gpu.luid, &name, &activate)) { printf("%s: no hardware encoder\n", Narrow(CodecName(codec)).c_str()); continue; }
        com_ptr<IMFTransform> mft;
        if (FAILED(activate->ActivateObject(IID_PPV_ARGS(mft.put())))) continue;
        auto api = mft.as<ICodecAPI>();
        printf("%s: %s\n", Narrow(CodecName(codec)).c_str(), Narrow(name).c_str());
        for (auto& p : props)
        {
            HRESULT s = api->IsSupported(p.key);
            VARIANT lo{}, hi{}, step{}, cur{};
            HRESULT r = s == S_OK ? api->GetParameterRange(p.key, &lo, &hi, &step) : E_FAIL;
            HRESULT g = s == S_OK ? api->GetValue(p.key, &cur) : E_FAIL;
            printf("  %-22s supported %s", p.name, s == S_OK ? "yes" : "no ");
            if (SUCCEEDED(r)) printf("  range %lu..%lu step %lu", lo.ulVal, hi.ulVal, step.ulVal);
            if (SUCCEEDED(g)) printf("  default %lu", cur.ulVal);
            printf("\n");
        }
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && std::wstring(argv[1]) == L"--dump")
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        MFStartup(MF_VERSION);
        Dump(argv[2]);
        return 0;
    }
    if (argc == 2 && std::wstring(argv[1]) == L"--codecapi")
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        MFStartup(MF_VERSION);
        CodecApi(*GpuDevice::Create());
        return 0;
    }
    setvbuf(stdout, nullptr, _IONBF, 0);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    Check(MFStartup(MF_VERSION), "Media Foundation");
    std::wstring dir, real;
    bool keep = false, hdrCase = true, quick = false;
    for (int i = 1; i < argc; ++i)
    {
        std::wstring a = argv[i];
        if (a == L"--dir" && i + 1 < argc) dir = argv[++i];
        else if (a == L"--real" && i + 1 < argc) real = argv[++i];
        else if (a == L"--keep") keep = true;
        else if (a == L"--no-hdr") hdrCase = false;
        else if (a == L"--quick") quick = true;
    }
    if (dir.empty())
    {
        wchar_t temp[MAX_PATH];
        GetTempPathW(MAX_PATH, temp);
        dir = std::wstring(temp) + L"ExoCaptureShrinkTest";
    }
    EnsureFolder(dir);
    try
    {
        auto gpu = GpuDevice::Create();
        auto support = ProbeEncoders(*gpu);
        printf("Exo Capture shrink self-test\nGPU: %s\nEncoders: H.264 %s, HEVC %s, AV1 %s (playback: HEVC %s, AV1 %s); best %s\n", Narrow(gpu->adapter).c_str(),
            support.h264 ? "yes" : "no", support.hevc ? "yes" : "no", support.av1 ? "yes" : "no", support.hevcPlayback ? "yes" : "no",
            support.av1Playback ? "yes" : "no", Narrow(CodecName(support.Best())).c_str());
        const std::string best = Narrow(CodecName(support.Best()));

        SsimUnit();

        // ---- the synthetic recording
        ClipSpec spec;
        if (quick) spec.seconds = 4;
        std::wstring clip = dir + L"\\synthetic 1080p60.mp4";
        printf("\n== making a %.0f s 1080p60 recording (%s, %.0f Mbit/s fixed rate, 2 AAC tracks, the recorder's own encoder)\n", spec.seconds,
            Narrow(CodecName(spec.codec)).c_str(), spec.bitrate / 1e6);
        int64_t t0 = Now100ns();
        MakeClip(*gpu, clip, spec);
        printf("  made in %.1f s: %s MB\n", (Now100ns() - t0) / 1e7, Mb(SizeOf(clip)).c_str());
        {
            Facts f = Measure(clip);
            printf("  info  %ux%u @ %.2f, %.2f s, %llu frames, %d audio tracks, key frame gap %.2f s\n", f.width, f.height, f.fps, f.seconds, f.videoSamples, f.audioTracks, f.maxKeyGap);
        }

        g_rows.push_back(ShrinkAndCheck(*gpu, "synthetic 1080p60, default quality", clip, ShrinkOptions{}, false, best));
        const std::wstring shrunk = clip.substr(0, clip.size() - 4) + L".shrink.mp4";

        // ---- the checks must be able to fail
        printf("\n== the verifier rejects bad files\n");
        {
            // Over-compressed: the same pipeline at a bit rate too low to hold the picture.
            ShrinkOptions poor;
            poor.input = clip;
            poor.forceBitrate = 350'000;
            std::wstring poorOut = dir + L"\\poor.mp4";
            poor.output = poorOut;
            ShrinkResult r = Shrink(poor);
            printf("      -> %s: %s (SSIM %.4f mean, %.4f lowest; the file was %.0f%% of the original)\n", r.skipped.c_str(), r.detail.c_str(), r.ssimMean, r.ssimMin,
                100.0 * r.outputBytes / std::max<uint64_t>(1, r.inputBytes));
            Expect(!r.ok && r.skipped == "quality-loss", "a file squeezed to 350 kbit/s is rejected as quality-loss");
            Expect(!Exists(poorOut) && !Exists(poorOut + L".part"), "and its output is deleted");
            Expect(r.ssimMean < ShrinkSsimMean || r.ssimMin < ShrinkSsimMin, Fmt("because its SSIM is %.4f / %.4f against the gates %.3f / %.2f", r.ssimMean, r.ssimMin, ShrinkSsimMean, ShrinkSsimMin));
        }
        {
            // A moderate squeeze, to place the gates between "fine" and "visibly worse".
            ShrinkOptions mid;
            mid.input = clip;
            mid.forceBitrate = 2'500'000;
            std::wstring midOut = dir + L"\\mid.mp4";
            mid.output = midOut;
            ShrinkResult r = Shrink(mid);
            printf("  info  2.5 Mbit/s: %s, SSIM %.4f mean, %.4f lowest, %.0f%% of the original\n", r.skipped.empty() ? "ok" : r.skipped.c_str(), r.ssimMean, r.ssimMin,
                100.0 * r.outputBytes / std::max<uint64_t>(1, r.inputBytes));
            DeleteFileW(midOut.c_str());
        }
        {
            std::wstring copy = dir + L"\\copy.mp4";
            CopyFileW(clip.c_str(), copy.c_str(), FALSE);
            Expect(Verdict(*gpu, clip, copy) == "not-smaller", "a copy of the original is not smaller");
            DeleteFileW(copy.c_str());
        }
        {
            std::wstring bad = dir + L"\\one-track.mp4";
            Remux(clip, bad, 1, -1);
            auto v = VerifyShrink(*gpu, clip, bad, nullptr, {}, -1.0);
            Expect(v.reason == "audio-tracks", "an audio track dropped: " + v.reason + " " + v.detail);
            DeleteFileW(bad.c_str());
        }
        {
            std::wstring bad = dir + L"\\changed-audio.mp4";
            Remux(clip, bad, -1, 1);
            auto v = VerifyShrink(*gpu, clip, bad, nullptr, {}, -1.0);
            Expect(v.reason == "audio-changed", "an audio packet altered: " + v.reason + " " + v.detail);
            DeleteFileW(bad.c_str());
        }
        {
            std::wstring bad = dir + L"\\short.mp4";
            DeleteFileW(bad.c_str());
            TrimCopy(clip, bad, 0, static_cast<int64_t>(spec.seconds * 0.6 * Second));
            auto v = VerifyShrink(*gpu, clip, bad, nullptr);
            Expect(v.reason == "duration", "a file cut to 60%: " + v.reason + " " + v.detail);
            DeleteFileW(bad.c_str());
        }
        {
            // The shrunk file against a recording of different content: the picture check, not the size one.
            ClipSpec other = spec;
            other.seconds = spec.seconds;
            other.toneHz = 440;
            other.bitrate = 3'000'000;
            std::wstring different = dir + L"\\different.mp4";
            // The same shape of file with another picture: a gradient-only clip made at a tenth of the rate.
            MakeClip(*gpu, different, other);
            auto v = VerifyShrink(*gpu, clip, different, nullptr);
            Expect(v.reason == "quality-loss" || v.reason == "audio-changed", "a lower-rate encode of the same scene: " + v.reason + " " + v.detail);
            DeleteFileW(different.c_str());
        }

        // ---- cancel
        printf("\n== cancel\n");
        {
            std::atomic<bool> cancel{ false };
            ShrinkOptions o;
            o.input = clip;
            std::wstring out = dir + L"\\cancelled.mp4";
            o.output = out;
            o.cancel = &cancel;
            int64_t asked = 0;
            double at = 0;
            o.progress = [&](double p) {
                if (!asked && p >= 20) { asked = Now100ns(); at = p; cancel = true; }
            };
            std::wstring part = out + L".part";
            ShrinkResult r = Shrink(o);
            double took = asked ? (Now100ns() - asked) / 1e7 : -1;
            Expect(asked != 0, Fmt("cancelled at %.0f%%", at));
            Expect(!r.ok && r.skipped == "cancelled", "the result says cancelled: " + r.skipped);
            Expect(took >= 0 && took < 1.0, Fmt("it returned %.3f s after the cancel (under 1 s)", took));
            Expect(!Exists(part) && !Exists(out), "the partial file is gone");
        }

        // ---- no GPU decoder
        if (!quick)
        {
            ShrinkOptions o;
            o.cpuDecode = true;
            g_rows.push_back(ShrinkAndCheck(*gpu, "synthetic, decoded on the CPU", clip, o, false, best));
        }

        // ---- no constant-quality mode, other codecs, the recorder's four tracks
        {
            ShrinkOptions o;
            o.forceVbr = true;
            g_rows.push_back(ShrinkAndCheck(*gpu, "synthetic, variable rate only (no quality mode)", clip, o, false, best));
        }
        {
            ClipSpec h264 = spec;
            h264.codec = Codec::H264;
            h264.seconds = 3;
            h264.bitrate = 30'000'000;
            std::wstring h264Clip = dir + L"\\synthetic h264.mp4";
            MakeClip(*gpu, h264Clip, h264);
            ShrinkOptions o;
            g_rows.push_back(ShrinkAndCheck(*gpu, "H.264 recording, codec auto", h264Clip, o, false, best));
            DeleteFileW((h264Clip.substr(0, h264Clip.size() - 4) + L".shrink.mp4").c_str());
            o.codec = L"h264";
            g_rows.push_back(ShrinkAndCheck(*gpu, "H.264 recording, codec h264", h264Clip, o, false, "H.264"));
            DeleteFileW(h264Clip.c_str());

            // Never a worse codec than the recording has.
            ShrinkOptions worse;
            worse.input = clip;
            worse.codec = L"h264";
            worse.output = dir + L"\\worse.mp4";
            ShrinkResult r = Shrink(worse);
            Expect(!r.ok && r.skipped == "no-better-encoder" && !Exists(worse.output), "an HEVC recording is not re-encoded as H.264: " + r.skipped);
        }
        {
            ClipSpec four = spec;
            four.audioTracks = 4;
            four.seconds = 3;
            std::wstring fourClip = dir + L"\\synthetic four tracks.mp4";
            MakeClip(*gpu, fourClip, four);
            ShrinkOptions o;
            g_rows.push_back(ShrinkAndCheck(*gpu, "four audio tracks, as the recorder writes", fourClip, o, false, best));
            DeleteFileW(fourClip.c_str());
        }

        // ---- quality range
        if (!quick)
        {
            for (int q : { 40, 90 })
            {
                ShrinkOptions o;
                o.input = clip;
                o.quality = q;
                o.output = dir + L"\\q" + std::to_wstring(q) + L".mp4";
                ShrinkResult r = Shrink(o);
                printf("  info  targetQuality %d: %s, %.1f MB, ratio %.3f, SSIM %.4f / %.4f, mode %s, %.1f s\n", q, r.skipped.empty() ? "ok" : r.skipped.c_str(), r.outputBytes / 1048576.0, r.ratio, r.ssimMean, r.ssimMin, r.mode.c_str(), r.seconds);
                DeleteFileW(o.output.c_str());
            }
        }

        // ---- HDR
        if (hdrCase)
        {
            ClipSpec hdr = spec;
            hdr.hdr = true;
            hdr.seconds = std::min(spec.seconds, 6.0);
            hdr.bitrate = 18'000'000;
            std::wstring hdrClip = dir + L"\\synthetic hdr.mp4";
            printf("\n== making a %.0f s HDR10 (P010, BT.2020, PQ) recording\n", hdr.seconds);
            try
            {
                MakeClip(*gpu, hdrClip, hdr);
                Facts f = Measure(hdrClip);
                printf("  info  %ux%u, transfer %u, primaries %u, matrix %u\n", f.width, f.height, f.transfer, f.primaries, f.matrix);
                if (f.transfer == MFVideoTransFunc_2084)
                {
                    ShrinkOptions o;
                    auto row = ShrinkAndCheck(*gpu, "synthetic HDR10", hdrClip, o, true, best);
                    g_rows.push_back(row);
                }
                else printf("  skipped: the test recording did not come out HDR (this encoder or the MP4 writer dropped the tags)\n");
            }
            catch (const std::exception& e)
            {
                printf("  the GPU cannot make an HDR test recording here: %s\n", e.what());
            }
        }

        // ---- a real recording
        if (!real.empty())
        {
            std::wstring copy = dir + L"\\real recording.mp4";
            if (CopyFileW(real.c_str(), copy.c_str(), FALSE))
            {
                ShrinkOptions o;
                g_rows.push_back(ShrinkAndCheck(*gpu, "real recording (" + Narrow(real.substr(real.find_last_of(L'\\') + 1)) + ")", copy, o, false, ""));
            }
            else printf("  could not copy %s\n", Narrow(real).c_str());
        }

        printf("\n%-44s %8s %8s %7s %8s %8s  %-26s %-8s %6s\n", "case", "in MB", "out MB", "ratio", "SSIM", "min", "encoder", "mode", "sec");
        for (auto& r : g_rows)
            printf("%-44s %8.2f %8.2f %7.3f %8.4f %8.4f  %-26s %-8s %6.1f  %s\n", r.name.c_str(), r.inMB, r.outMB, r.ratio, r.mean, r.min, r.encoder.c_str(), r.mode.c_str(), r.seconds, r.note.c_str());
    }
    catch (const std::exception& e)
    {
        printf("\nFAILED with an error: %s\n", e.what());
        ++g_failed;
    }
    if (!keep)
    {
        WIN32_FIND_DATAW found{};
        HANDLE find = FindFirstFileW((dir + L"\\*").c_str(), &found);
        if (find != INVALID_HANDLE_VALUE)
        {
            do DeleteFileW((dir + L"\\" + found.cFileName).c_str());
            while (FindNextFileW(find, &found));
            FindClose(find);
        }
        RemoveDirectoryW(dir.c_str());
    }
    printf("\n%d checks, %d failed\n", g_checks, g_failed);
    MFShutdown();
    return g_failed ? 1 : 0;
}
