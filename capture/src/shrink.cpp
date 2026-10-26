// Exo Capture - shrink: re-encodes a finished recording to a much smaller file at the same look, and proves it did.
// GPL-3.0-or-later.
//
// The live recorder holds a fixed bit rate with a hardware encoder that has no time to think. Here there is time:
// the recording is decoded (on the GPU where Windows can), re-encoded with B-frames, a normal GOP and the encoder's
// slowest setting at a constant quality (or, where the encoder has no such mode, a rate near 60% of the original's),
// and every audio track is copied through packet for packet. Nothing is reported ok until the new file has been
// decoded end to end and compared with the old one; the old one is never touched.
#include "shrink.h"

#include <propvarutil.h>

#include <algorithm>
#include <cmath>
#include <map>

namespace
{
constexpr int64_t Second = 10'000'000;
// A new file is built under this name and renamed once it passes, so a listing never shows a half-written video.
constexpr wchar_t PartExt[] = L".part";

struct Cancelled {};
/// A reason to leave the recording alone, not an error.
struct Skip
{
    std::string reason, detail;
};
/// The recording could not be read or decoded (another encoder would not help).
struct DecodeFailure : Failure
{
    DecodeFailure(HRESULT hr, const char* doing) : Failure(hr, doing) {}
};

/// Cancellation and progress for one run; progress is told at most twice a second.
struct Run
{
    const std::atomic<bool>* cancel = nullptr;
    std::function<void(double)> progress;
    int64_t lastReport = 0;
    void Poll() const
    {
        if (cancel && cancel->load()) throw Cancelled{};
    }
    void Report(double percent)
    {
        if (!progress) return;
        int64_t now = Now100ns();
        if (lastReport && now - lastReport < Second / 2) return;
        lastReport = now;
        progress(std::clamp(percent, 0.0, 100.0));
    }
};

/// Below-normal priority for this thread while it shrinks; live capture runs in this process and must not feel it.
struct LowPriority
{
    int old = GetThreadPriority(GetCurrentThread());
    LowPriority() { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL); }
    ~LowPriority() { SetThreadPriority(GetCurrentThread(), old); }
};

uint64_t FileSize(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA info{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info)) return 0;
    return static_cast<uint64_t>(info.nFileSizeHigh) << 32 | info.nFileSizeLow;
}

bool Exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

std::wstring Lower(std::wstring s)
{
    CharLowerBuffW(s.data(), static_cast<DWORD>(s.size()));
    return s;
}

std::string FourCc(uint32_t v)
{
    std::string s(4, '?');
    for (int i = 0; i < 4; ++i)
    {
        char c = static_cast<char>(v >> (24 - 8 * i));
        s[i] = c >= 32 && c < 127 ? c : '?';
    }
    return s;
}

// ---------------------------------------------------------------- the MP4's own boxes

// Windows' MP4 source shows a track only if it is video or sound, and its media type leaves out what the file's
// boxes say about bit depth and HDR metadata. A few reads of the boxes tell what Media Foundation will not.
constexpr uint32_t Cc(const char (&s)[5])
{
    return static_cast<uint32_t>(static_cast<uint8_t>(s[0])) << 24 | static_cast<uint32_t>(static_cast<uint8_t>(s[1])) << 16 |
        static_cast<uint32_t>(static_cast<uint8_t>(s[2])) << 8 | static_cast<uint8_t>(s[3]);
}

uint32_t Be32(const uint8_t* p) { return static_cast<uint32_t>(p[0]) << 24 | static_cast<uint32_t>(p[1]) << 16 | static_cast<uint32_t>(p[2]) << 8 | p[3]; }
uint64_t Be64(const uint8_t* p) { return static_cast<uint64_t>(Be32(p)) << 32 | Be32(p + 4); }

class BoxFile
{
public:
    explicit BoxFile(const std::wstring& path)
    {
        m_file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, nullptr);
        LARGE_INTEGER size{};
        if (m_file != INVALID_HANDLE_VALUE && GetFileSizeEx(m_file, &size)) m_size = static_cast<uint64_t>(size.QuadPart);
    }
    ~BoxFile()
    {
        if (m_file != INVALID_HANDLE_VALUE) CloseHandle(m_file);
    }
    BoxFile(const BoxFile&) = delete;
    BoxFile& operator=(const BoxFile&) = delete;
    uint64_t Size() const { return m_size; }
    bool Read(uint64_t at, void* out, size_t n) const
    {
        OVERLAPPED where{};
        where.Offset = static_cast<DWORD>(at);
        where.OffsetHigh = static_cast<DWORD>(at >> 32);
        DWORD got = 0;
        return m_file != INVALID_HANDLE_VALUE && ReadFile(m_file, out, static_cast<DWORD>(n), &got, &where) && got == n;
    }

private:
    HANDLE m_file = INVALID_HANDLE_VALUE;
    uint64_t m_size = 0;
};

/// Calls visit(type, bodyStart, bodyEnd) for each box in [pos, end) until it returns false. A damaged file just ends it.
template <class F>
void EachBox(const BoxFile& file, uint64_t pos, uint64_t end, F&& visit)
{
    for (int n = 0; n < 100000 && pos + 8 <= end; ++n)
    {
        uint8_t head[16];
        if (!file.Read(pos, head, 8)) return;
        uint64_t size = Be32(head), header = 8;
        uint32_t type = Be32(head + 4);
        if (size == 1)
        {
            if (!file.Read(pos + 8, head + 8, 8)) return;
            size = Be64(head + 8);
            header = 16;
        }
        else if (size == 0) size = end - pos;
        if (size < header || size > end - pos) return;
        if (!visit(type, pos + header, pos + size)) return;
        pos += size;
    }
}

bool Child(const BoxFile& file, uint64_t body, uint64_t end, uint32_t want, uint64_t& childBody, uint64_t& childEnd)
{
    bool found = false;
    EachBox(file, body, end, [&](uint32_t type, uint64_t b, uint64_t e) {
        if (type != want) return true;
        childBody = b;
        childEnd = e;
        found = true;
        return false;
    });
    return found;
}

struct Mp4Video
{
    uint32_t entry = 0;
    int bitDepth = 0; // 0: not stated
    std::vector<uint8_t> mdcv, clli;
};

struct Mp4Info
{
    bool ok = false;
    std::vector<uint32_t> handlers; // every track's kind ('vide', 'soun', ...), in file order
    Mp4Video video;
    int Count(uint32_t kind) const { return static_cast<int>(std::count(handlers.begin(), handlers.end(), kind)); }
};

int BitDepth(uint32_t config, const std::vector<uint8_t>& p)
{
    if (config == Cc("hvcC") && p.size() >= 19) return (p[17] & 7) + 8;
    if (config == Cc("av1C") && p.size() >= 4) return (p[2] & 0x20) ? 12 : (p[2] & 0x40) ? 10 : 8;
    if (config == Cc("avcC") && p.size() >= 7)
    {
        uint8_t profile = p[1];
        if (profile != 100 && profile != 110 && profile != 122 && profile != 244) return 8;
        size_t at = 6;
        for (int set = 0; set < 2 && at + 1 <= p.size(); ++set)
        {
            size_t count = set == 0 ? (p[5] & 31) : p[at++];
            for (size_t i = 0; i < count && at + 2 <= p.size(); ++i) at += 2 + (static_cast<size_t>(p[at]) << 8 | p[at + 1]);
        }
        return at + 2 <= p.size() ? (p[at + 1] & 7) + 8 : 8;
    }
    return 0;
}

void ReadTrack(const BoxFile& file, uint64_t body, uint64_t end, Mp4Info& info, bool& haveVideo)
{
    uint64_t b = 0, e = 0;
    if (!Child(file, body, end, Cc("mdia"), b, e)) return;
    const uint64_t mdiaB = b, mdiaE = e;
    uint32_t handler = 0;
    uint8_t four[4];
    if (Child(file, mdiaB, mdiaE, Cc("hdlr"), b, e) && e - b >= 12 && file.Read(b + 8, four, 4)) handler = Be32(four);
    info.handlers.push_back(handler);
    if (handler != Cc("vide") || haveVideo) return;
    haveVideo = true;
    uint64_t minfB, minfE, stblB, stblE, stsdB, stsdE;
    if (!Child(file, mdiaB, mdiaE, Cc("minf"), minfB, minfE) || !Child(file, minfB, minfE, Cc("stbl"), stblB, stblE) ||
        !Child(file, stblB, stblE, Cc("stsd"), stsdB, stsdE) || stsdE - stsdB < 8)
        return;
    // The first sample entry: 78 bytes of picture fields, then boxes (the codec's configuration, color, HDR).
    EachBox(file, stsdB + 8, stsdE, [&](uint32_t type, uint64_t entryB, uint64_t entryE) {
        info.video.entry = type;
        if (entryE - entryB < 78) return false;
        EachBox(file, entryB + 78, entryE, [&](uint32_t kind, uint64_t cb, uint64_t ce) {
            if (ce - cb > 16384) return true;
            std::vector<uint8_t> payload(static_cast<size_t>(ce - cb));
            if (!payload.empty() && !file.Read(cb, payload.data(), payload.size())) return true;
            if (kind == Cc("hvcC") || kind == Cc("avcC") || kind == Cc("av1C")) info.video.bitDepth = BitDepth(kind, payload);
            else if (kind == Cc("mdcv")) info.video.mdcv = std::move(payload);
            else if (kind == Cc("clli")) info.video.clli = std::move(payload);
            return true;
        });
        return false;
    });
}

Mp4Info ReadMp4(const std::wstring& path)
{
    Mp4Info info;
    BoxFile file(path);
    bool haveVideo = false;
    EachBox(file, 0, file.Size(), [&](uint32_t type, uint64_t body, uint64_t end) {
        if (type != Cc("moov")) return true;
        info.ok = true;
        EachBox(file, body, end, [&](uint32_t kind, uint64_t trackB, uint64_t trackE) {
            if (kind == Cc("trak")) ReadTrack(file, trackB, trackE, info, haveVideo);
            return true;
        });
        return false;
    });
    return info;
}

// ---------------------------------------------------------------- reading

struct Track
{
    DWORD index = 0;
    GUID major{};
    GUID sub{};
    com_ptr<IMFMediaType> type;
};

struct Layout
{
    Track video;
    std::vector<Track> other; // sound and anything else the file carries, in file order
    bool moreVideo = false;
    int64_t duration = 0;
};

/// A reader of the file. With a GPU it decodes there (samples arrive as GPU surfaces); without one, on the CPU.
com_ptr<IMFSourceReader> OpenReader(const std::wstring& path, GpuDevice* gpu)
{
    com_ptr<IMFByteStream> stream;
    HRESULT hr = MFCreateFile(MF_ACCESSMODE_READ, MF_OPENMODE_FAIL_IF_NOT_EXIST, MF_FILEFLAGS_NONE, path.c_str(), stream.put());
    if (FAILED(hr)) throw DecodeFailure(hr, "open the video");
    // The file being checked is named .part: say what it holds instead of leaving Windows to guess from the name.
    if (auto attrs = stream.try_as<IMFAttributes>()) attrs->SetString(MF_BYTESTREAM_CONTENT_TYPE, L"video/mp4");
    com_ptr<IMFAttributes> attrs;
    Check(MFCreateAttributes(attrs.put(), 3), "reader attributes");
    if (gpu) attrs->SetUnknown(MF_SOURCE_READER_D3D_MANAGER, gpu->manager.get());
    attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, gpu ? TRUE : FALSE);
    com_ptr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromByteStream(stream.get(), attrs.get(), reader.put());
    if (FAILED(hr)) throw DecodeFailure(hr, "open the video");
    return reader;
}

Layout ReadLayout(IMFSourceReader* reader)
{
    Layout layout;
    for (DWORD i = 0;; ++i)
    {
        com_ptr<IMFMediaType> type;
        HRESULT hr = reader->GetNativeMediaType(i, 0, type.put());
        if (hr == MF_E_INVALIDSTREAMNUMBER) break;
        if (FAILED(hr)) throw DecodeFailure(hr, "read the video's tracks");
        Track t;
        t.index = i;
        t.type = type;
        type->GetMajorType(&t.major);
        type->GetGUID(MF_MT_SUBTYPE, &t.sub);
        if (t.major == MFMediaType_Video)
        {
            if (layout.video.type) layout.moreVideo = true;
            else layout.video = t;
        }
        else layout.other.push_back(t);
    }
    PROPVARIANT value;
    PropVariantInit(&value);
    if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &value)))
        layout.duration = static_cast<int64_t>(value.uhVal.QuadPart);
    PropVariantClear(&value);
    return layout;
}

/// The picture's size: the display aperture where the file states one (an encoder codes 1080 lines as 1088 and says so).
void PictureSize(IMFMediaType* type, UINT& width, UINT& height)
{
    MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height);
    MFVideoArea area{};
    if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr)) && area.Area.cx > 0 &&
        area.Area.cy > 0 && static_cast<UINT>(area.Area.cx) <= width && static_cast<UINT>(area.Area.cy) <= height)
    {
        width = static_cast<UINT>(area.Area.cx);
        height = static_cast<UINT>(area.Area.cy);
    }
}

/// The attributes that say how to read the colors; a copy must keep every one the recording has.
const GUID* const ColorKeys[] = {
    &MF_MT_VIDEO_PRIMARIES, &MF_MT_TRANSFER_FUNCTION, &MF_MT_YUV_MATRIX, &MF_MT_VIDEO_NOMINAL_RANGE,
    &MF_MT_MAX_LUMINANCE_LEVEL, &MF_MT_MAX_FRAME_AVERAGE_LUMINANCE_LEVEL, &MF_MT_MAX_MASTERING_LUMINANCE, &MF_MT_MIN_MASTERING_LUMINANCE,
};

void CopyColor(IMFMediaType* from, IMFMediaType* to)
{
    for (auto key : ColorKeys)
    {
        UINT32 v = 0;
        if (SUCCEEDED(from->GetUINT32(*key, &v))) to->SetUINT32(*key, v);
    }
    UINT32 size = 0;
    if (SUCCEEDED(from->GetBlobSize(MF_MT_CUSTOM_VIDEO_PRIMARIES, &size)) && size > 0 && size <= 256)
    {
        std::vector<UINT8> blob(size);
        if (SUCCEEDED(from->GetBlob(MF_MT_CUSTOM_VIDEO_PRIMARIES, blob.data(), size, nullptr))) to->SetBlob(MF_MT_CUSTOM_VIDEO_PRIMARIES, blob.data(), size);
    }
}

/// The first color attribute the recording states that the new file does not state the same; empty when none.
std::string ColorDiff(IMFMediaType* oldType, IMFMediaType* newType)
{
    static const char* const names[] = { "primaries", "transfer function", "color matrix", "range", "MaxCLL", "MaxFALL", "mastering max", "mastering min" };
    for (size_t i = 0; i < std::size(ColorKeys); ++i)
    {
        UINT32 a = 0, b = 0;
        if (FAILED(oldType->GetUINT32(*ColorKeys[i], &a))) continue;
        if (FAILED(newType->GetUINT32(*ColorKeys[i], &b))) return std::string(names[i]) + " is missing";
        if (a != b) return std::string(names[i]) + " is " + std::to_string(b) + ", was " + std::to_string(a);
    }
    return {};
}

// ---------------------------------------------------------------- picture quality
}

double Ssim(const uint16_t* a, const uint16_t* b, UINT width, UINT height, UINT range)
{
    constexpr UINT window = 8, step = 4;
    const double c1 = (0.01 * range) * (0.01 * range), c2 = (0.03 * range) * (0.03 * range);
    double total = 0;
    uint64_t count = 0;
    for (UINT y = 0; y + window <= height; y += step)
        for (UINT x = 0; x + window <= width; x += step)
        {
            uint64_t sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            for (UINT j = 0; j < window; ++j)
            {
                const uint16_t* pa = a + static_cast<size_t>(y + j) * width + x;
                const uint16_t* pb = b + static_cast<size_t>(y + j) * width + x;
                for (UINT i = 0; i < window; ++i)
                {
                    uint64_t va = pa[i], vb = pb[i];
                    sa += va;
                    sb += vb;
                    saa += va * va;
                    sbb += vb * vb;
                    sab += va * vb;
                }
            }
            const double n = window * window;
            double ma = sa / n, mb = sb / n;
            double va = saa / n - ma * ma, vb = sbb / n - mb * mb, cov = sab / n - ma * mb;
            total += ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
            ++count;
        }
    return count ? total / count : 1.0;
}

namespace
{
/// Luma of decoded frames, read back from the GPU (or the CPU's own buffer).
class LumaReader
{
public:
    explicit LumaReader(GpuDevice& gpu) : m_gpu(gpu) {}

    /// w x h samples, tight; 8-bit values as they are, 10-bit ones brought down to 0..1023.
    void Read(IMFSample* sample, UINT w, UINT h, bool tenBit, std::vector<uint16_t>& plane)
    {
        com_ptr<IMFMediaBuffer> buffer;
        Check(sample->GetBufferByIndex(0, buffer.put()), "frame");
        plane.resize(static_cast<size_t>(w) * h);
        const UINT bytes = tenBit ? 2 : 1;
        if (auto dxgi = buffer.try_as<IMFDXGIBuffer>())
        {
            com_ptr<ID3D11Texture2D> texture;
            Check(dxgi->GetResource(IID_PPV_ARGS(texture.put())), "frame texture");
            UINT index = 0;
            dxgi->GetSubresourceIndex(&index);
            D3D11_TEXTURE2D_DESC desc{};
            texture->GetDesc(&desc);
            if (desc.Width < w || desc.Height < h) throw Failure(E_FAIL, "frame smaller than the video");
            if (!m_staging || m_desc.Width != desc.Width || m_desc.Height != desc.Height || m_desc.Format != desc.Format)
            {
                m_desc = desc;
                m_desc.MipLevels = 1;
                m_desc.ArraySize = 1;
                m_desc.Usage = D3D11_USAGE_STAGING;
                m_desc.BindFlags = 0;
                m_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                m_desc.MiscFlags = 0;
                m_staging = nullptr;
                Check(m_gpu.device->CreateTexture2D(&m_desc, nullptr, m_staging.put()), "frame readback texture");
            }
            m_gpu.context->CopySubresourceRegion(m_staging.get(), 0, 0, 0, 0, texture.get(), index, nullptr);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            Check(m_gpu.context->Map(m_staging.get(), 0, D3D11_MAP_READ, 0, &mapped), "frame readback");
            Unpack(static_cast<const uint8_t*>(mapped.pData), mapped.RowPitch, w, h, bytes, plane);
            m_gpu.context->Unmap(m_staging.get(), 0);
            return;
        }
        BYTE* src = nullptr;
        LONG pitch = 0;
        auto twoD = buffer.try_as<IMF2DBuffer>();
        DWORD length = 0;
        if (twoD) Check(twoD->Lock2D(&src, &pitch), "frame lock");
        else
        {
            Check(buffer->Lock(&src, nullptr, &length), "frame lock");
            pitch = static_cast<LONG>(w * bytes);
        }
        Unpack(src, static_cast<UINT>(pitch), w, h, bytes, plane);
        if (twoD) twoD->Unlock2D();
        else buffer->Unlock();
    }

private:
    static void Unpack(const uint8_t* src, UINT pitch, UINT w, UINT h, UINT bytes, std::vector<uint16_t>& plane)
    {
        for (UINT y = 0; y < h; ++y)
        {
            const uint8_t* row = src + static_cast<size_t>(y) * pitch;
            uint16_t* out = plane.data() + static_cast<size_t>(y) * w;
            if (bytes == 1)
                for (UINT x = 0; x < w; ++x) out[x] = row[x];
            else
                for (UINT x = 0; x < w; ++x) out[x] = static_cast<uint16_t>((row[x * 2] | row[x * 2 + 1] << 8) >> 6);
        }
    }

    GpuDevice& m_gpu;
    D3D11_TEXTURE2D_DESC m_desc{};
    com_ptr<ID3D11Texture2D> m_staging;
};

// ---------------------------------------------------------------- the encoder

struct VideoParams
{
    UINT width = 0, height = 0, fpsNum = 60, fpsDen = 1;
    bool tenBit = false;
    com_ptr<IMFMediaType> source; // the recording's own video type: its color attributes carry over
};

HRESULT SetU32(ICodecAPI* api, const GUID& key, UINT32 value)
{
    VARIANT v{};
    v.vt = VT_UI4;
    v.ulVal = value;
    return api->SetValue(&key, &v);
}

HRESULT SetFlag(ICodecAPI* api, const GUID& key, bool value)
{
    VARIANT v{};
    v.vt = VT_BOOL;
    v.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
    return api->SetValue(&key, &v);
}

/// The GPU's hardware encoder for the file, run for quality: B-frames, a 3 s GOP, its slowest setting.
class Encoder
{
public:
    struct Event
    {
        MediaEventType type;
        HRESULT status;
    };

    Encoder(GpuDevice& gpu, Codec codec, const VideoParams& video, int quality, UINT meanBitrate, UINT forcedBitrate, bool vbrOnly)
        : m_codec(codec), m_box(std::make_shared<Mailbox>())
    {
        const GUID input = video.tenBit ? MFVideoFormat_P010 : MFVideoFormat_NV12;
        const UINT32 flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;
        com_ptr<IMFActivate> activate;
        if (!FindMft(MFT_CATEGORY_VIDEO_ENCODER, flags, input, Subtype(codec), &gpu.luid, &m_name, &activate) &&
            !FindMft(MFT_CATEGORY_VIDEO_ENCODER, flags, input, Subtype(codec), nullptr, &m_name, &activate))
            throw Failure(MF_E_TOPO_CODEC_NOT_FOUND, "no hardware video encoder");
        Check(activate->ActivateObject(IID_PPV_ARGS(m_mft.put())), "start the video encoder");
        com_ptr<IMFAttributes> attrs;
        if (SUCCEEDED(m_mft->GetAttributes(attrs.put()))) attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE); // not low latency: that turns B-frames off
        Check(m_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(gpu.manager.get())), "encoder GPU");
        if (m_mft->GetStreamIDs(1, &m_in, 1, &m_out) != S_OK) { m_in = 0; m_out = 0; }

        const UINT fps = std::max<UINT>(1, (video.fpsNum + video.fpsDen / 2) / std::max<UINT>(1, video.fpsDen));
        m_api = m_mft.as<ICodecAPI>();
        // Constant quality where the encoder has it; else a peak-constrained VBR near 60% of the recording's own rate.
        bool set = false;
        if (!forcedBitrate && !vbrOnly && SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_Quality)) &&
            SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncCommonQuality, static_cast<UINT32>(std::clamp(quality, 0, 100)))))
        {
            m_mode = "quality";
            set = true;
        }
        if (!set)
        {
            if (SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_PeakConstrainedVBR))) m_mode = "vbr";
            else if (SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_UnconstrainedVBR))) m_mode = "vbr";
            else if (SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR))) m_mode = "cbr";
            else m_mode = "default";
            SetU32(m_api.get(), CODECAPI_AVEncCommonMeanBitRate, forcedBitrate ? forcedBitrate : meanBitrate);
            SetU32(m_api.get(), CODECAPI_AVEncCommonMaxBitRate, static_cast<UINT32>(std::min<double>((forcedBitrate ? forcedBitrate : meanBitrate) * 1.5, 200e6)));
        }
        // A key frame every 3 s keeps seeking quick; the B-frames between them are where most of the saving is.
        m_gop = SUCCEEDED(SetU32(m_api.get(), CODECAPI_AVEncMPVGOPSize, fps * 3));
        // (NVIDIA's encoders say they have no B-frames setting; Intel's and AMD's do.)
        m_bHr = m_api->IsSupported(&CODECAPI_AVEncMPVDefaultBPictureCount) == S_OK ? SetU32(m_api.get(), CODECAPI_AVEncMPVDefaultBPictureCount, 2) : E_NOTIMPL;
        m_bFrames = SUCCEEDED(m_bHr);
        // More reference frames let a picture borrow from further back, which a still or slow-moving screen rewards.
        if (m_api->IsSupported(&CODECAPI_AVEncVideoMaxNumRefFrame) == S_OK) SetU32(m_api.get(), CODECAPI_AVEncVideoMaxNumRefFrame, 4);
        SetFlag(m_api.get(), CODECAPI_AVLowLatencyMode, false);
        // Offline there is time to spend: the encoder's most careful setting (the live recorder uses 40).
        SetU32(m_api.get(), CODECAPI_AVEncCommonQualityVsSpeed, 100);

        com_ptr<IMFMediaType> out;
        Check(MFCreateMediaType(out.put()), "media type");
        out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        out->SetGUID(MF_MT_SUBTYPE, Subtype(codec));
        out->SetUINT32(MF_MT_AVG_BITRATE, forcedBitrate ? forcedBitrate : meanBitrate);
        Format(out.get(), video);
        if (video.tenBit)
            out->SetUINT32(MF_MT_MPEG2_PROFILE, codec == Codec::AV1 ? eAVEncAV1VProfile_Main_420_10 : eAVEncH265VProfile_Main_420_10);
        else
            out->SetUINT32(MF_MT_MPEG2_PROFILE, codec == Codec::AV1 ? eAVEncAV1VProfile_Main_420_8
                : codec == Codec::HEVC ? eAVEncH265VProfile_Main_420_8 : eAVEncH264VProfile_High);
        Check(m_mft->SetOutputType(m_out, out.get(), 0), "encoder output type");

        com_ptr<IMFMediaType> in;
        for (DWORD i = 0;; ++i)
        {
            com_ptr<IMFMediaType> candidate;
            if (FAILED(m_mft->GetInputAvailableType(m_in, i, candidate.put()))) break;
            GUID sub{};
            candidate->GetGUID(MF_MT_SUBTYPE, &sub);
            if (sub == input) { in = candidate; break; }
        }
        if (!in)
        {
            Check(MFCreateMediaType(in.put()), "media type");
            in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            in->SetGUID(MF_MT_SUBTYPE, input);
        }
        Format(in.get(), video);
        Check(m_mft->SetInputType(m_in, in.get(), 0), "encoder input type");

        m_events = m_mft.as<IMFMediaEventGenerator>();
        m_mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        Check(m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "encoder start");
        Check(m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "encoder stream start");
        m_thread = std::thread([this] { Events(); });
    }

    ~Encoder()
    {
        if (m_mft)
        {
            m_mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            if (auto shutdown = m_mft.try_as<IMFShutdown>()) shutdown->Shutdown();
        }
        if (m_thread.joinable()) m_thread.join();
        if (m_mft) m_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, 0);
    }
    Encoder(const Encoder&) = delete;
    Encoder& operator=(const Encoder&) = delete;

    const std::wstring& Name() const { return m_name; }
    const std::string& Mode() const { return m_mode; }
    std::string Settings() const
    {
        return "rate control " + m_mode + ", GOP " + (m_gop ? "3 s" : "default") + ", B-frames " + (m_bFrames ? std::string("2") : "not offered");
    }

    /// The next event of the encoder, waiting up to a while; a timeout (or a wake) gives MEUnknown.
    Event Wait(std::chrono::milliseconds wait)
    {
        std::unique_lock lock(m_box->lock);
        m_box->changed.wait_for(lock, wait, [this] { return !m_box->queue.empty() || m_box->woken; });
        m_box->woken = false;
        if (m_box->queue.empty()) return { MEUnknown, S_OK };
        auto event = m_box->queue.front();
        m_box->queue.pop_front();
        return event;
    }
    /// Makes a Wait end now. Safe after the encoder is gone (a frame can be let go of while it shuts down).
    std::function<void()> Waker()
    {
        return [box = m_box] {
            std::lock_guard lock(box->lock);
            box->woken = true;
            box->changed.notify_all();
        };
    }

    void Input(IMFSample* sample) { Check(m_mft->ProcessInput(m_in, sample, 0), "encode"); }

    void Drain()
    {
        m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, m_in);
        m_mft->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, m_in);
    }

    /// One encoded sample, or null when there is none now.
    com_ptr<IMFSample> Output()
    {
        MFT_OUTPUT_STREAM_INFO info{};
        m_mft->GetOutputStreamInfo(m_out, &info);
        MFT_OUTPUT_DATA_BUFFER output{};
        output.dwStreamID = m_out;
        com_ptr<IMFSample> own;
        if (!(info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)))
        {
            com_ptr<IMFMediaBuffer> buffer;
            Check(MFCreateMemoryBuffer(std::max<DWORD>(info.cbSize, 4 * 1024 * 1024), buffer.put()), "output buffer");
            Check(MFCreateSample(own.put()), "output sample");
            own->AddBuffer(buffer.get());
            output.pSample = own.get();
        }
        DWORD status = 0;
        HRESULT hr = m_mft->ProcessOutput(0, 1, &output, &status);
        if (output.pEvents) output.pEvents->Release();
        com_ptr<IMFSample> sample;
        if (own) sample = own;
        else if (output.pSample) sample.attach(output.pSample);
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            com_ptr<IMFMediaType> type;
            if (SUCCEEDED(m_mft->GetOutputAvailableType(m_out, 0, type.put()))) m_mft->SetOutputType(m_out, type.get(), 0);
            return {};
        }
        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return {};
        Check(hr, "encode");
        return sample;
    }

    /// The type the encoder produces, for the MP4: its own, with the picture's size and the sequence header.
    com_ptr<IMFMediaType> OutputType(const VideoParams& video, const std::vector<uint8_t>& firstKeyFrame)
    {
        com_ptr<IMFMediaType> current, type;
        Check(m_mft->GetOutputCurrentType(m_out, current.put()), "encoder type");
        Check(MFCreateMediaType(type.put()), "media type");
        current->CopyAllItems(type.get());
        // The file states the picture's own size; the encoder reports its padded block size.
        MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, video.width, video.height);
        type->DeleteItem(MF_MT_MINIMUM_DISPLAY_APERTURE);
        type->DeleteItem(MF_MT_GEOMETRIC_APERTURE);
        UINT32 headerSize = 0;
        if (FAILED(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerSize)) || headerSize == 0)
        {
            auto sets = ParameterSets(firstKeyFrame, m_codec);
            if (!sets.empty()) type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, sets.data(), static_cast<UINT32>(sets.size()));
        }
        if (video.source) CopyColor(video.source.get(), type.get());
        return type;
    }

private:
    static void Format(IMFMediaType* type, const VideoParams& video)
    {
        MFSetAttributeSize(type, MF_MT_FRAME_SIZE, video.width, video.height);
        MFSetAttributeRatio(type, MF_MT_FRAME_RATE, video.fpsNum, video.fpsDen);
        MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (video.source) CopyColor(video.source.get(), type);
    }

    void Events()
    {
        for (;;)
        {
            com_ptr<IMFMediaEvent> event;
            if (FAILED(m_events->GetEvent(0, event.put()))) break;
            Event e{ MEUnknown, S_OK };
            event->GetType(&e.type);
            event->GetStatus(&e.status);
            std::lock_guard lock(m_box->lock);
            m_box->queue.push_back(e);
            m_box->changed.notify_all();
        }
    }

    Codec m_codec;
    com_ptr<IMFTransform> m_mft;
    com_ptr<IMFMediaEventGenerator> m_events;
    com_ptr<ICodecAPI> m_api;
    DWORD m_in = 0, m_out = 0;
    std::wstring m_name;
    std::string m_mode;
    bool m_gop = false, m_bFrames = false;
    HRESULT m_bHr = S_OK;
    struct Mailbox
    {
        std::mutex lock;
        std::condition_variable changed;
        std::deque<Event> queue;
        bool woken = false;
    };
    std::shared_ptr<Mailbox> m_box;
    std::thread m_thread;
};

// ---------------------------------------------------------------- the new file

/// The MP4 the encoder's packets and the original audio packets go into. It opens on the first encoded key frame (that
/// is what carries the video's sequence header); sound read before then waits.
class Sink
{
public:
    Sink(const std::wstring& path, const Layout& layout) : m_path(path), m_layout(layout) {}
    ~Sink() { Abandon(); }
    bool IsOpen() const { return m_writer != nullptr; }

    void Open(IMFMediaType* video)
    {
        com_ptr<IMFAttributes> attrs;
        Check(MFCreateAttributes(attrs.put(), 3), "writer attributes");
        attrs->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
        attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
        attrs->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
        Check(MFCreateSinkWriterFromURL(m_path.c_str(), nullptr, attrs.get(), m_writer.put()), "new MP4 file");
        DWORD index = 0;
        Check(m_writer->AddStream(video, &index), "video track");
        Check(m_writer->SetInputMediaType(index, video, nullptr), "video track type");
        // Each audio (and any other) track as it came: the same type in, the same type out, no codec between. Windows'
        // MP4 source lists tracks from the last to the first, so the file's order is the reverse of the reader's.
        for (auto it = m_layout.other.rbegin(); it != m_layout.other.rend(); ++it)
        {
            const Track& t = *it;
            if (FAILED(m_writer->AddStream(t.type.get(), &index)) || FAILED(m_writer->SetInputMediaType(index, t.type.get(), nullptr)))
                throw Skip{ t.major == MFMediaType_Audio ? "audio-passthrough" : "unsupported-tracks",
                    "track " + std::to_string(t.index) + " cannot be copied as it is" };
        }
        Check(m_writer->BeginWriting(), "MP4 start");
        for (auto& p : m_pending) Send(p.first, p.second.get());
        m_pending.clear();
    }

    void Write(DWORD stream, IMFSample* sample)
    {
        if (m_writer)
        {
            Send(stream, sample);
            return;
        }
        com_ptr<IMFSample> held;
        held.copy_from(sample);
        m_pending.emplace_back(stream, std::move(held));
    }

    void Finish()
    {
        if (!m_writer) throw Failure(E_FAIL, "nothing was encoded");
        Check(m_writer->Finalize(), "MP4 finish");
        m_writer = nullptr;
    }

    /// Lets go of the file without finishing it (a cancel or a failure): the caller deletes it.
    void Abandon() { m_writer = nullptr; }

private:
    void Send(DWORD stream, IMFSample* sample) { Check(m_writer->WriteSample(stream, sample), "write sample"); }

    std::wstring m_path;
    const Layout& m_layout;
    com_ptr<IMFSinkWriter> m_writer;
    std::vector<std::pair<DWORD, com_ptr<IMFSample>>> m_pending;
};

// Hands a texture back when the encoder is done with the frame that held it.
struct ReleaseCallback : winrt::implements<ReleaseCallback, IMFAsyncCallback>
{
    std::function<void()> done;
    explicit ReleaseCallback(std::function<void()> fn) : done(std::move(fn)) {}
    HRESULT __stdcall GetParameters(DWORD*, DWORD*) noexcept override { return E_NOTIMPL; }
    HRESULT __stdcall Invoke(IMFAsyncResult*) noexcept override
    {
        if (done) done();
        done = nullptr;
        return S_OK;
    }
};

/// The encoder's own copies of the decoded frames. The decoder's few surfaces cannot be held while an encoder looks
/// ahead (it would stall the decoder), so each frame is copied on the GPU into a texture from here; the texture comes
/// back when the encoder lets go of the sample. Bounded: at most a few hundred MB of video memory.
class FramePool
{
public:
    FramePool(GpuDevice& gpu, UINT width, UINT height, bool tenBit, std::function<void()> wake)
        : m_gpu(gpu), m_width(width), m_height(height), m_tenBit(tenBit), m_wake(std::move(wake)),
          m_free(std::make_shared<std::vector<int>>()), m_lock(std::make_shared<std::mutex>())
    {
        const size_t bytes = static_cast<size_t>(static_cast<double>(width) * height * (tenBit ? 3.0 : 1.5));
        m_cap = std::clamp<size_t>((640u << 20) / std::max<size_t>(bytes, 1), 12, 40);
    }

    /// The decoded frame copied into a pool texture and wrapped for the encoder; null while all are in use.
    com_ptr<IMFSample> Take(IMFSample* decoded, int64_t time, int64_t duration)
    {
        int slot = Acquire();
        if (slot < 0) return {};
        try
        {
            Copy(decoded, m_textures[slot].get());
            com_ptr<IMFMediaBuffer> buffer;
            Check(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), m_textures[slot].get(), 0, FALSE, buffer.put()), "frame buffer");
            if (auto twoD = buffer.try_as<IMF2DBuffer>())
            {
                DWORD length = 0;
                if (SUCCEEDED(twoD->GetContiguousLength(&length))) buffer->SetCurrentLength(length);
            }
            com_ptr<IMFTrackedSample> tracked;
            Check(MFCreateTrackedSample(tracked.put()), "frame sample");
            auto sample = tracked.as<IMFSample>();
            sample->AddBuffer(buffer.get());
            sample->SetSampleTime(time);
            sample->SetSampleDuration(duration);
            auto free = m_free;
            auto lock = m_lock;
            auto wake = m_wake;
            auto callback = winrt::make<ReleaseCallback>([free, lock, wake, slot] {
                { std::lock_guard guard(*lock); free->push_back(slot); }
                wake();
            });
            tracked->SetAllocator(callback.as<IMFAsyncCallback>().get(), nullptr);
            return sample;
        }
        catch (...)
        {
            std::lock_guard guard(*m_lock);
            m_free->push_back(slot);
            throw;
        }
    }

private:
    int Acquire()
    {
        {
            std::lock_guard guard(*m_lock);
            if (!m_free->empty())
            {
                int slot = m_free->back();
                m_free->pop_back();
                return slot;
            }
        }
        if (m_textures.size() >= m_cap) return -1;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = m_width;
        desc.Height = m_height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = m_tenBit ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        com_ptr<ID3D11Texture2D> texture;
        Check(m_gpu.device->CreateTexture2D(&desc, nullptr, texture.put()), "frame texture");
        m_textures.push_back(texture);
        return static_cast<int>(m_textures.size()) - 1;
    }

    void Copy(IMFSample* decoded, ID3D11Texture2D* target)
    {
        com_ptr<IMFMediaBuffer> buffer;
        Check(decoded->GetBufferByIndex(0, buffer.put()), "frame");
        if (auto dxgi = buffer.try_as<IMFDXGIBuffer>())
        {
            // GPU to GPU: the decoder's surface (a slice of its texture array) into ours.
            com_ptr<ID3D11Texture2D> source;
            Check(dxgi->GetResource(IID_PPV_ARGS(source.put())), "frame texture");
            UINT index = 0;
            dxgi->GetSubresourceIndex(&index);
            D3D11_TEXTURE2D_DESC desc{};
            source->GetDesc(&desc);
            if (desc.Width < m_width || desc.Height < m_height) throw DecodeFailure(E_FAIL, "decoded frame smaller than the video");
            D3D11_BOX box{ 0, 0, 0, m_width, m_height, 1 };
            m_gpu.context->CopySubresourceRegion(target, 0, 0, 0, 0, source.get(), index, &box);
            m_gpu.context->Flush();
            return;
        }
        // The CPU decoded it (no GPU decoder for this codec): up through a staging texture.
        if (!m_upload)
        {
            D3D11_TEXTURE2D_DESC desc{};
            target->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            Check(m_gpu.device->CreateTexture2D(&desc, nullptr, m_upload.put()), "frame upload texture");
        }
        BYTE* src = nullptr;
        LONG pitch = 0;
        auto twoD = buffer.try_as<IMF2DBuffer>();
        DWORD length = 0;
        const UINT bytes = m_tenBit ? 2 : 1;
        if (twoD) Check(twoD->Lock2D(&src, &pitch), "frame lock");
        else
        {
            Check(buffer->Lock(&src, nullptr, &length), "frame lock");
            pitch = static_cast<LONG>(m_width * bytes);
        }
        // The chroma plane follows the luma plane, whose rows the decoder may have padded.
        DWORD contiguous = 0;
        if (twoD) twoD->GetContiguousLength(&contiguous);
        else contiguous = length;
        UINT paddedRows = contiguous ? static_cast<UINT>(contiguous * 2ull / 3 / std::abs(pitch)) : m_height;
        paddedRows = std::max(paddedRows, m_height);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT hr = m_gpu.context->Map(m_upload.get(), 0, D3D11_MAP_WRITE, 0, &mapped);
        if (SUCCEEDED(hr))
        {
            auto* dst = static_cast<uint8_t*>(mapped.pData);
            for (UINT y = 0; y < m_height; ++y) memcpy(dst + static_cast<size_t>(y) * mapped.RowPitch, src + static_cast<size_t>(y) * pitch, m_width * bytes);
            auto* chromaSrc = src + static_cast<size_t>(paddedRows) * pitch;
            auto* chromaDst = dst + static_cast<size_t>(m_height) * mapped.RowPitch;
            for (UINT y = 0; y < m_height / 2; ++y) memcpy(chromaDst + static_cast<size_t>(y) * mapped.RowPitch, chromaSrc + static_cast<size_t>(y) * pitch, m_width * bytes);
            m_gpu.context->Unmap(m_upload.get(), 0);
            m_gpu.context->CopyResource(target, m_upload.get());
            m_gpu.context->Flush();
        }
        if (twoD) twoD->Unlock2D();
        else buffer->Unlock();
        Check(hr, "frame upload");
    }

    GpuDevice& m_gpu;
    UINT m_width, m_height;
    bool m_tenBit;
    std::function<void()> m_wake;
    size_t m_cap = 12;
    std::vector<com_ptr<ID3D11Texture2D>> m_textures;
    std::shared_ptr<std::vector<int>> m_free;
    std::shared_ptr<std::mutex> m_lock;
    com_ptr<ID3D11Texture2D> m_upload;
};

// ---------------------------------------------------------------- what the recording is

struct Source
{
    Layout layout;
    Mp4Info mp4;
    Codec codec = Codec::H264;
    VideoParams video;
    bool hdr = false;
    uint64_t bytes = 0;
    UINT videoBitrate = 0;
};

bool IsHdr(const Mp4Info& mp4, IMFMediaType* type)
{
    UINT32 transfer = MFGetAttributeUINT32(type, MF_MT_TRANSFER_FUNCTION, 0), primaries = MFGetAttributeUINT32(type, MF_MT_VIDEO_PRIMARIES, 0);
    return transfer == MFVideoTransFunc_2084 || transfer == MFVideoTransFunc_HLG || primaries == MFVideoPrimaries_BT2020 ||
        mp4.video.bitDepth > 8 || !mp4.video.mdcv.empty() || !mp4.video.clli.empty();
}

Source Inspect(const std::wstring& path)
{
    Source s;
    s.bytes = FileSize(path);
    s.mp4 = ReadMp4(path);
    if (!s.mp4.ok) throw Skip{ "not-mp4", "the file has no movie box" };
    // Every track must be one we can carry: a track Windows would not show, or a kind the new file cannot hold,
    // would be dropped silently, so the whole recording is left alone instead.
    for (auto kind : s.mp4.handlers)
        if (kind != Cc("vide") && kind != Cc("soun")) throw Skip{ "unsupported-tracks", "the file has a '" + FourCc(kind) + "' track" };
    if (s.mp4.Count(Cc("vide")) != 1) throw Skip{ "unsupported-tracks", "the file has " + std::to_string(s.mp4.Count(Cc("vide"))) + " video tracks" };
    auto reader = OpenReader(path, nullptr);
    s.layout = ReadLayout(reader.get());
    if (!s.layout.video.type || s.layout.moreVideo || s.layout.other.size() + 1 != s.mp4.handlers.size())
        throw Skip{ "unsupported-tracks", "Windows shows " + std::to_string(s.layout.other.size() + (s.layout.video.type ? 1 : 0)) + " of the file's " +
            std::to_string(s.mp4.handlers.size()) + " tracks" };
    IMFMediaType* native = s.layout.video.type.get();
    const GUID sub = s.layout.video.sub;
    if (sub == MFVideoFormat_H264 || sub == MFVideoFormat_H264_ES) s.codec = Codec::H264;
    else if (sub == MFVideoFormat_HEVC || sub == MFVideoFormat_HEVC_ES) s.codec = Codec::HEVC;
    else if (sub == MFVideoFormat_AV1) s.codec = Codec::AV1;
    else throw Skip{ "unsupported-video", "the video is not H.264, HEVC or AV1" };
    PictureSize(native, s.video.width, s.video.height);
    if (!s.video.width || !s.video.height || (s.video.width | s.video.height) & 1) throw Skip{ "unsupported-video", "the picture size is odd or missing" };
    UINT32 num = 0, den = 0;
    if (SUCCEEDED(MFGetAttributeRatio(native, MF_MT_FRAME_RATE, &num, &den)) && num && den) { s.video.fpsNum = num; s.video.fpsDen = den; }
    s.video.source.copy_from(native);

    UINT32 transfer = MFGetAttributeUINT32(native, MF_MT_TRANSFER_FUNCTION, 0);
    bool pq = transfer == MFVideoTransFunc_2084 || transfer == MFVideoTransFunc_HLG;
    int depth = s.mp4.video.bitDepth ? s.mp4.video.bitDepth : (pq ? 10 : 8);
    if (depth > 10) throw Skip{ "hdr-unsupported", std::to_string(depth) + "-bit video" };
    s.video.tenBit = depth > 8;
    s.hdr = IsHdr(s.mp4, native);
    if (s.video.tenBit && s.codec == Codec::H264) throw Skip{ "hdr-unsupported", "10-bit H.264 has no hardware encoder to go back to" };

    double seconds = s.layout.duration / 1e7;
    if (seconds <= 0) throw Skip{ "unsupported-video", "the file has no duration" };
    double audioBits = 0;
    for (auto& t : s.layout.other) audioBits += 8.0 * MFGetAttributeUINT32(t.type.get(), MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
    s.videoBitrate = static_cast<UINT>(std::max(1e5, s.bytes * 8.0 / seconds - audioBits));
    return s;
}

/// The codecs to try, best first: nothing above what was asked, nothing the GPU lacks or Windows could not play back,
/// nothing worse than the recording already is.
std::vector<Codec> Ladder(const std::wstring& want, const EncoderSupport& support, const Source& source)
{
    Codec ceiling = Codec::AV1;
    std::wstring w = Lower(want);
    if (w == L"hevc" || w == L"h265") ceiling = Codec::HEVC;
    else if (w == L"h264") ceiling = Codec::H264;
    auto usable = [&](Codec c) {
        if (c == Codec::AV1) return support.av1 && support.av1Playback;
        if (c == Codec::HEVC) return support.hevc && support.hevcPlayback;
        return support.h264 && !source.video.tenBit;
    };
    std::vector<Codec> out;
    for (Codec c : { Codec::AV1, Codec::HEVC, Codec::H264 })
        if (c <= ceiling && c >= source.codec && usable(c)) out.push_back(c);
    return out;
}

// ---------------------------------------------------------------- shrinking

class Job
{
public:
    Job(const ShrinkOptions& options, Run& run, GpuDevice& gpu, const Source& source, std::wstring temp)
        : m_opt(options), m_run(run), m_gpu(gpu), m_src(source), m_temp(std::move(temp)) {}

    /// Encodes the recording into the temp file with this codec; throws a Failure when the encoder cannot.
    void Transcode(Codec codec)
    {
        m_framesIn = 0;
        const Layout& layout = m_src.layout;
        const VideoParams& video = m_src.video;
        auto reader = OpenReader(m_opt.input, m_opt.cpuDecode ? nullptr : &m_gpu);
        reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
        com_ptr<IMFMediaType> want;
        Check(MFCreateMediaType(want.put()), "media type");
        want->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        want->SetGUID(MF_MT_SUBTYPE, video.tenBit ? MFVideoFormat_P010 : MFVideoFormat_NV12);
        HRESULT hr = reader->SetCurrentMediaType(layout.video.index, nullptr, want.get());
        if (FAILED(hr)) throw DecodeFailure(hr, "decode the video");
        reader->SetStreamSelection(layout.video.index, TRUE);
        std::map<DWORD, DWORD> outputOf; // reader stream -> track in the new file
        std::map<DWORD, bool> finished;
        outputOf[layout.video.index] = 0;
        finished[layout.video.index] = false;
        for (size_t k = 0; k < layout.other.size(); ++k)
        {
            const Track& t = layout.other[k];
            if (FAILED(reader->SetCurrentMediaType(t.index, nullptr, t.type.get())))
                throw Skip{ "audio-passthrough", "track " + std::to_string(t.index) + " cannot be read as it is" };
            reader->SetStreamSelection(t.index, TRUE);
            outputOf[t.index] = static_cast<DWORD>(layout.other.size() - k); // the sink adds them last first
            finished[t.index] = false;
        }

        // Rate for the encoders with no constant-quality mode: near 60% of the recording's own, never above 85%.
        double share = std::min(0.85, 0.55 + 0.10 * std::clamp(m_opt.quality, 0, 100) / 100.0);
        UINT mean = static_cast<UINT>(std::max(5e5, m_src.videoBitrate * share));

        DeleteFileW(m_temp.c_str());
        Sink sink(m_temp, layout);
        Encoder encoder(m_gpu, codec, video, m_opt.quality, mean, m_opt.forceBitrate, m_opt.forceVbr);
        FramePool pool(m_gpu, video.width, video.height, video.tenBit, encoder.Waker());
        m_encoder = encoder.Name();
        m_mode = encoder.Mode();
        Log("Shrink: " + Narrow(CodecName(codec)) + " on " + Narrow(m_encoder) + ", " + encoder.Settings() + ".");

        const int64_t nominal = Second * video.fpsDen / std::max<UINT>(1, video.fpsNum);
        int credits = 0;
        bool videoEnded = false, drained = false, starved = false;
        int64_t starvedSince = 0;
        com_ptr<IMFSample> frame; // decoded, waiting for a texture of the pool
        int64_t frameTime = 0, frameDuration = 0;
        std::vector<uint8_t> firstKey;

        auto othersDone = [&] {
            for (auto& [index, done] : finished)
                if (index != layout.video.index && !done) return false;
            return true;
        };
        // One sample from the file: video is kept in `frame`; sound goes straight into the new file as it is.
        auto readOne = [&]() {
            DWORD actual = 0, flags = 0;
            LONGLONG time = 0;
            com_ptr<IMFSample> sample;
            HRESULT read = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &actual, &flags, &time, sample.put());
            if (FAILED(read) || (flags & MF_SOURCE_READERF_ERROR)) throw DecodeFailure(FAILED(read) ? read : E_FAIL, "decode");
            auto it = outputOf.find(actual);
            if (it == outputOf.end()) return;
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
            {
                finished[actual] = true;
                return;
            }
            if (!sample) return;
            if (actual == layout.video.index)
            {
                frame = sample;
                frameTime = time;
                LONGLONG d = 0;
                frameDuration = SUCCEEDED(sample->GetSampleDuration(&d)) && d > 0 ? d : nominal;
                return;
            }
            sink.Write(it->second, sample.get());
        };
        auto onOutput = [&](IMFSample* sample) {
            com_ptr<IMFMediaBuffer> buffer;
            Check(sample->ConvertToContiguousBuffer(buffer.put()), "encoded data");
            BYTE* bytes = nullptr;
            DWORD length = 0;
            Check(buffer->Lock(&bytes, nullptr, &length), "encoded lock");
            if (!sink.IsOpen())
            {
                firstKey.assign(bytes, bytes + length);
                buffer->Unlock();
                auto type = encoder.OutputType(video, firstKey);
                sink.Open(type.get());
            }
            else buffer->Unlock();
            sink.Write(0, sample);
        };

        for (;;)
        {
            m_run.Poll();
            const bool busy = (!videoEnded && credits > 0 && !starved) || (videoEnded && !othersDone());
            auto event = encoder.Wait(std::chrono::milliseconds(busy ? 0 : 20));
            if (event.type == MEError) throw Failure(FAILED(event.status) ? event.status : E_FAIL, "encoder");
            if (event.type == METransformNeedInput) ++credits;
            else if (event.type == METransformHaveOutput)
            {
                if (auto out = encoder.Output()) onOutput(out.get());
            }
            else if (event.type == METransformDrainComplete) drained = true;

            while (!videoEnded && credits > 0)
            {
                if (!frame)
                {
                    while (!frame && !finished[layout.video.index]) readOne();
                    if (!frame)
                    {
                        videoEnded = true;
                        encoder.Drain();
                        break;
                    }
                }
                auto sample = pool.Take(frame.get(), frameTime, frameDuration);
                if (!sample)
                {
                    // Every texture is with the encoder: wait for one to come back.
                    if (!starved) starvedSince = Now100ns();
                    starved = true;
                    if (Now100ns() - starvedSince > 60 * Second) throw Failure(E_FAIL, "encoder stalled");
                    break;
                }
                starved = false;
                encoder.Input(sample.get());
                ++m_framesIn;
                --credits;
                m_run.Report(90.0 * std::min(1.0, static_cast<double>(frameTime) / std::max<int64_t>(1, layout.duration)));
                frame = nullptr;
            }
            if (videoEnded && !othersDone()) readOne();
            if (drained && othersDone()) break;
        }
        sink.Finish();
    }

    uint64_t FramesIn() const { return m_framesIn; }
    const std::wstring& EncoderName() const { return m_encoder; }
    const std::string& Mode() const { return m_mode; }

private:
    const ShrinkOptions& m_opt;
    Run& m_run;
    GpuDevice& m_gpu;
    const Source& m_src;
    std::wstring m_temp;
    uint64_t m_framesIn = 0;
    std::wstring m_encoder;
    std::string m_mode;
};

// ---------------------------------------------------------------- checking

/// Every packet of one sound track in the old file against the same track in the new one: the same bytes at the same
/// times. Empty when they are the same, else what differs.
std::string CompareAudio(const std::wstring& oldPath, const Track& oldTrack, const std::wstring& newPath, const Track& newTrack, Run& run)
{
    auto open = [](const std::wstring& path, const Track& t) {
        auto reader = OpenReader(path, nullptr);
        reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
        HRESULT hr = reader->SetCurrentMediaType(t.index, nullptr, t.type.get());
        if (FAILED(hr)) throw DecodeFailure(hr, "read audio");
        reader->SetStreamSelection(t.index, TRUE);
        return reader;
    };
    auto next = [](IMFSourceReader* reader, DWORD stream, std::vector<uint8_t>& bytes, int64_t& time) {
        for (;;)
        {
            DWORD actual = 0, flags = 0;
            LONGLONG t = 0;
            com_ptr<IMFSample> sample;
            HRESULT hr = reader->ReadSample(stream, 0, &actual, &flags, &t, sample.put());
            if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) throw DecodeFailure(FAILED(hr) ? hr : E_FAIL, "read audio");
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) return false;
            if (!sample) continue;
            com_ptr<IMFMediaBuffer> buffer;
            Check(sample->ConvertToContiguousBuffer(buffer.put()), "audio packet");
            BYTE* data = nullptr;
            DWORD length = 0;
            Check(buffer->Lock(&data, nullptr, &length), "audio packet");
            bytes.assign(data, data + length);
            buffer->Unlock();
            time = t;
            return true;
        }
    };
    if (oldTrack.sub != newTrack.sub || MFGetAttributeUINT32(oldTrack.type.get(), MF_MT_AUDIO_NUM_CHANNELS, 0) != MFGetAttributeUINT32(newTrack.type.get(), MF_MT_AUDIO_NUM_CHANNELS, 0) ||
        MFGetAttributeUINT32(oldTrack.type.get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, 0) != MFGetAttributeUINT32(newTrack.type.get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, 0))
        return "its format changed";
    auto a = open(oldPath, oldTrack), b = open(newPath, newTrack);
    std::vector<uint8_t> pa, pb;
    int64_t ta = 0, tb = 0;
    for (uint64_t n = 0;; ++n)
    {
        run.Poll();
        bool ha = next(a.get(), oldTrack.index, pa, ta), hb = next(b.get(), newTrack.index, pb, tb);
        if (ha != hb) return "it has " + std::string(ha ? "fewer" : "more") + " packets (" + std::to_string(n) + " in the shorter one)";
        if (!ha) return {};
        if (pa != pb) return "packet " + std::to_string(n) + " differs";
        if (std::abs(ta - tb) > 20'000) return "packet " + std::to_string(n) + " moved by " + std::to_string(std::abs(ta - tb) / 10'000.0) + " ms";
    }
}

/// A video read frame by frame in presentation order.
struct FrameStream
{
    com_ptr<IMFSourceReader> reader;
    DWORD stream = 0;
    com_ptr<IMFSample> sample;
    int64_t time = 0;
    uint64_t count = 0;
    bool live = true;

    bool Next(Run& run)
    {
        for (;;)
        {
            run.Poll();
            DWORD actual = 0, flags = 0;
            LONGLONG t = 0;
            com_ptr<IMFSample> s;
            HRESULT hr = reader->ReadSample(stream, 0, &actual, &flags, &t, s.put());
            if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) throw DecodeFailure(FAILED(hr) ? hr : MF_E_INVALID_STREAM_DATA, "decode");
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
            {
                live = false;
                return false;
            }
            if (!s) continue;
            sample = s;
            time = t;
            ++count;
            return true;
        }
    }
};

FrameStream OpenFrames(const std::wstring& path, GpuDevice& gpu, bool tenBit, Layout& layout)
{
    FrameStream f;
    f.reader = OpenReader(path, &gpu);
    layout = ReadLayout(f.reader.get());
    if (!layout.video.type) throw DecodeFailure(E_FAIL, "the file has no video");
    f.stream = layout.video.index;
    f.reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    com_ptr<IMFMediaType> want;
    Check(MFCreateMediaType(want.put()), "media type");
    want->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    want->SetGUID(MF_MT_SUBTYPE, tenBit ? MFVideoFormat_P010 : MFVideoFormat_NV12);
    HRESULT hr = f.reader->SetCurrentMediaType(f.stream, nullptr, want.get());
    if (FAILED(hr)) throw DecodeFailure(hr, "decode the video");
    f.reader->SetStreamSelection(f.stream, TRUE);
    return f;
}
}

ShrinkVerdict VerifyShrink(GpuDevice& gpu, const std::wstring& input, const std::wstring& output, const std::atomic<bool>* cancel,
    const std::function<void(double)>& progress, double minSaving)
{
    ShrinkVerdict v;
    Run run{ cancel, progress };
    auto fail = [&](const char* reason, std::string detail) {
        v.reason = reason;
        v.detail = std::move(detail);
        return v;
    };
    try
    {
        const uint64_t oldBytes = FileSize(input), newBytes = FileSize(output);
        if (!newBytes) return fail("output-missing", "the new file is empty or missing");
        if (static_cast<double>(newBytes) > static_cast<double>(oldBytes) * (1.0 - minSaving))
        {
            char text[96];
            sprintf_s(text, "the new file is %.0f%% of the old; at most %.0f%% keeps it", 100.0 * newBytes / std::max<uint64_t>(1, oldBytes), 100.0 * (1.0 - minSaving));
            return fail("not-smaller", text);
        }

        // Tracks: one video and the same sound tracks, in the same order.
        Mp4Info oldMp4 = ReadMp4(input), newMp4 = ReadMp4(output);
        if (!newMp4.ok) return fail("output-unreadable", "the new file has no movie box");
        if (oldMp4.Count(Cc("soun")) != newMp4.Count(Cc("soun")) || oldMp4.handlers.size() != newMp4.handlers.size())
            return fail("audio-tracks", std::to_string(oldMp4.Count(Cc("soun"))) + " sound tracks became " + std::to_string(newMp4.Count(Cc("soun"))));

        Layout oldLayout, newLayout;
        {
            auto a = OpenReader(input, nullptr), b = OpenReader(output, nullptr);
            oldLayout = ReadLayout(a.get());
            newLayout = ReadLayout(b.get());
        }
        if (!newLayout.video.type) return fail("output-unreadable", "the new file has no video");
        if (oldLayout.other.size() != newLayout.other.size())
            return fail("audio-tracks", std::to_string(oldLayout.other.size()) + " sound tracks became " + std::to_string(newLayout.other.size()));
        if (std::abs(oldLayout.duration - newLayout.duration) > static_cast<int64_t>(ShrinkDurationSlack * Second))
            return fail("duration", "the video is " + std::to_string(oldLayout.duration / 1e7) + " s, the new file " + std::to_string(newLayout.duration / 1e7) + " s");

        // How the colors are to be read, and the bit depth: an HDR picture that lost either is not the picture.
        const bool hdr = IsHdr(oldMp4, oldLayout.video.type.get());
        const char* colorReason = hdr ? "hdr-unsupported" : "color-mismatch";
        const bool tenBit = oldMp4.video.bitDepth > 8 || (oldMp4.video.bitDepth == 0 && MFGetAttributeUINT32(oldLayout.video.type.get(), MF_MT_TRANSFER_FUNCTION, 0) == MFVideoTransFunc_2084);
        if (newMp4.video.bitDepth && (newMp4.video.bitDepth > 8) != tenBit)
            return fail(colorReason, std::to_string(newMp4.video.bitDepth) + "-bit video came out of a " + std::string(tenBit ? "10" : "8") + "-bit one");
        if (auto diff = ColorDiff(oldLayout.video.type.get(), newLayout.video.type.get()); !diff.empty()) return fail(colorReason, diff);
        if (oldMp4.video.mdcv != newMp4.video.mdcv) return fail("hdr-unsupported", "the mastering display metadata was not kept");
        if (oldMp4.video.clli != newMp4.video.clli) return fail("hdr-unsupported", "the content light level metadata was not kept");

        // Sound: every packet of every track is the same packet.
        for (size_t k = 0; k < oldLayout.other.size(); ++k)
        {
            if (auto diff = CompareAudio(input, oldLayout.other[k], output, newLayout.other[k], run); !diff.empty())
                return fail("audio-changed", "track " + std::to_string(k + 1) + ": " + diff);
            run.Report(90.0 + 1.0 * (k + 1) / oldLayout.other.size());
        }

        // Picture: decode both files whole, side by side, and compare 24 frames spread over the length.
        Layout l1, l2;
        FrameStream oldV = OpenFrames(input, gpu, tenBit, l1);
        FrameStream newV = OpenFrames(output, gpu, tenBit, l2);
        UINT w = 0, h = 0, num = 60, den = 1;
        PictureSize(l1.video.type.get(), w, h);
        MFGetAttributeRatio(l1.video.type.get(), MF_MT_FRAME_RATE, &num, &den);
        UINT nw = 0, nh = 0;
        PictureSize(l2.video.type.get(), nw, nh);
        if (nw != w || nh != h) return fail("picture-size", std::to_string(w) + "x" + std::to_string(h) + " became " + std::to_string(nw) + "x" + std::to_string(nh));
        if (!num || !den) { num = 60; den = 1; }
        const int64_t tolerance = std::max<int64_t>(Second * den / num / 2, 10'000);
        const double expected = std::max(1.0, oldLayout.duration / 1e7 * num / den);
        const UINT range = tenBit ? 1023 : 255;

        if (!oldV.Next(run)) return fail("decode-error", "the old video has no frames");
        if (!newV.Next(run)) return fail("decode-error", "the new video has no frames");
        const int64_t first = oldV.time;
        LumaReader luma(gpu);
        std::vector<uint16_t> pa, pb;
        std::vector<double> scores;
        for (int i = 0; i < ShrinkSamples; ++i)
        {
            int64_t target = first + (oldLayout.duration - first) * (2 * i + 1) / (2 * ShrinkSamples);
            while (oldV.live && oldV.time < target) oldV.Next(run);
            if (!oldV.live) break;
            while (newV.live && newV.time < oldV.time - tolerance) newV.Next(run);
            if (!newV.live) return fail("frame-count", "the new video ends before " + std::to_string(oldV.time / 1e7) + " s");
            if (std::abs(newV.time - oldV.time) > tolerance)
                return fail("frame-time", "the frame at " + std::to_string(oldV.time / 1e7) + " s is at " + std::to_string(newV.time / 1e7) + " s in the new video");
            luma.Read(oldV.sample.get(), w, h, tenBit, pa);
            luma.Read(newV.sample.get(), w, h, tenBit, pb);
            scores.push_back(Ssim(pa.data(), pb.data(), w, h, range));
            run.Report(91.0 + 4.0 * (i + 1) / ShrinkSamples);
            if (!oldV.Next(run)) break; // never the same frame twice
            if (!newV.live) break;
        }
        // The rest of both, decoded and counted (the whole new file must decode).
        uint64_t counted = 0;
        while (oldV.live) { oldV.Next(run); if ((++counted & 255) == 0) run.Report(95.0 + 4.0 * std::min(1.0, oldV.count / expected)); }
        while (newV.live) newV.Next(run);
        v.framesIn = oldV.count;
        v.framesOut = newV.count;
        const size_t needed = static_cast<size_t>(std::min<uint64_t>(ShrinkSamples, oldV.count)) * 5 / 6;
        if (scores.size() < std::max<size_t>(1, needed)) return fail("decode-error", "only " + std::to_string(scores.size()) + " frames could be compared");
        double sum = 0, lowest = 1;
        for (double s : scores) { sum += s; lowest = std::min(lowest, s); }
        v.ssimMean = sum / scores.size();
        v.ssimMin = lowest;
        if (std::abs(static_cast<double>(newV.count) - static_cast<double>(oldV.count)) > std::max(1.0, ShrinkFrameSlack * oldV.count))
            return fail("frame-count", std::to_string(oldV.count) + " frames became " + std::to_string(newV.count));
        if (v.ssimMean < ShrinkSsimMean || v.ssimMin < ShrinkSsimMin)
        {
            char text[96];
            sprintf_s(text, "SSIM %.4f mean and %.4f lowest of %zu frames; %.3f and %.2f are needed", v.ssimMean, v.ssimMin, scores.size(), ShrinkSsimMean, ShrinkSsimMin);
            return fail("quality-loss", text);
        }
    }
    catch (const DecodeFailure& e)
    {
        return fail("decode-error", e.what());
    }
    catch (const Cancelled&)
    {
        return fail("cancelled", "");
    }
    return v;
}

// ---------------------------------------------------------------- shrink

namespace
{
std::wstring DefaultOutput(const std::wstring& input)
{
    auto dot = input.find_last_of(L'.');
    auto slash = input.find_last_of(L"\\/");
    bool hasExt = dot != std::wstring::npos && (slash == std::wstring::npos || dot > slash);
    return (hasExt ? input.substr(0, dot) : input) + L".shrink.mp4";
}

std::wstring FullName(const std::wstring& path)
{
    wchar_t buffer[MAX_PATH * 4];
    DWORD n = GetFullPathNameW(path.c_str(), ARRAYSIZE(buffer), buffer, nullptr);
    return n && n < ARRAYSIZE(buffer) ? std::wstring(buffer, n) : path;
}
}

ShrinkResult Shrink(const ShrinkOptions& options)
{
    ShrinkResult r;
    r.input = options.input;
    const int64_t started = Now100ns();
    const std::wstring input = FullName(options.input);
    const bool named = !options.output.empty();
    const std::wstring output = named ? FullName(options.output) : DefaultOutput(input);
    const std::wstring temp = output + PartExt;
    r.output = output;
    try
    {
        LowPriority low;
        Run run{ options.cancel, options.progress };
        run.Poll();
        r.inputBytes = FileSize(input);
        if (!r.inputBytes) throw Skip{ "not-found", "the file is missing or empty" };
        if (Lower(input) == Lower(output)) throw Skip{ "bad-output", "the output is the input" };
        const std::wstring lowered = Lower(input);
        if (lowered.size() >= 11 && lowered.compare(lowered.size() - 11, 11, L".shrink.mp4") == 0) throw Skip{ "already-shrunk", "this is a shrunk file" };
        if (Exists(input + L".exo-rec")) throw Skip{ "recording-in-progress", "the recording is still open or waits to be recovered" };
        if (named && Exists(output)) throw Skip{ "output-exists", "the output file exists" };

        auto gpu = GpuDevice::Create();
        auto support = ProbeEncoders(*gpu);
        Source source = Inspect(input);
        auto ladder = Ladder(options.codec, support, source);
        if (ladder.empty()) throw Skip{ source.video.tenBit ? "hdr-unsupported" : "no-better-encoder", "no encoder here beats the recording's own" };

        ShrinkOptions job = options;
        job.input = input;
        Job work(job, run, *gpu, source, temp);
        std::string lastError;
        bool encoded = false;
        for (Codec codec : ladder)
        {
            try
            {
                work.Transcode(codec);
                r.codec = CodecName(codec);
                encoded = true;
                break;
            }
            catch (const DecodeFailure&) { throw; }
            catch (const Failure& e)
            {
                // This encoder would not do it (or died on it): the next one down the ladder.
                lastError = std::string(Narrow(CodecName(codec))) + ": " + e.what();
                Log("Shrink: " + lastError);
                DeleteFileW(temp.c_str());
            }
        }
        if (!encoded) throw Skip{ source.video.tenBit ? "hdr-unsupported" : "no-encoder", lastError };
        r.encoder = work.EncoderName();
        r.mode = work.Mode();

        auto verdict = VerifyShrink(*gpu, input, temp, options.cancel, [&](double p) { run.Report(p); });
        r.ssimMean = verdict.ssimMean;
        r.ssimMin = verdict.ssimMin;
        r.framesIn = verdict.framesIn;
        r.framesOut = verdict.framesOut;
        r.outputBytes = FileSize(temp);
        if (verdict.reason == "cancelled") throw Cancelled{};
        if (!verdict.reason.empty()) throw Skip{ verdict.reason, verdict.detail };
        run.Poll();
        if (!MoveFileExW(temp.c_str(), output.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw Failure(HRESULT_FROM_WIN32(GetLastError()), "name the new file");
        r.outputBytes = FileSize(output);
        r.ratio = static_cast<double>(r.outputBytes) / static_cast<double>(r.inputBytes);
        r.ok = true;
    }
    catch (const Cancelled&)
    {
        DeleteFileW(temp.c_str());
        r.skipped = "cancelled";
    }
    catch (const Skip& s)
    {
        DeleteFileW(temp.c_str());
        r.skipped = s.reason;
        r.detail = s.detail;
    }
    catch (const DecodeFailure& e)
    {
        DeleteFileW(temp.c_str());
        r.skipped = "source-unreadable";
        r.detail = e.what();
    }
    catch (...)
    {
        DeleteFileW(temp.c_str());
        throw;
    }
    if (!r.ok && r.inputBytes && r.outputBytes) r.ratio = static_cast<double>(r.outputBytes) / static_cast<double>(r.inputBytes);
    if (!r.ok) r.output.clear();
    r.seconds = (Now100ns() - started) / 1e7;
    return r;
}

json::JsonObject ShrinkJson(const ShrinkResult& r)
{
    auto num = [](double v) { return json::JsonValue::CreateNumberValue(v); };
    auto str = [](const std::wstring& s) { return json::JsonValue::CreateStringValue(s); };
    auto o = Obj();
    o.Insert(L"ok", json::JsonValue::CreateBooleanValue(r.ok));
    o.Insert(L"input", str(r.input));
    o.Insert(L"output", str(r.output));
    o.Insert(L"inputBytes", num(static_cast<double>(r.inputBytes)));
    o.Insert(L"outputBytes", num(static_cast<double>(r.ok ? r.outputBytes : 0)));
    o.Insert(L"ratio", num(r.ok ? r.ratio : 0));
    o.Insert(L"codec", str(r.codec));
    o.Insert(L"seconds", num(r.seconds));
    auto ssim = Obj();
    ssim.Insert(L"mean", num(r.ssimMean));
    ssim.Insert(L"min", num(r.ssimMin));
    o.Insert(L"ssim", ssim);
    if (!r.encoder.empty()) o.Insert(L"encoder", str(r.encoder));
    if (!r.mode.empty()) o.Insert(L"mode", str(Widen(r.mode)));
    if (r.framesIn) o.Insert(L"frames", num(static_cast<double>(r.framesIn)));
    if (!r.skipped.empty()) o.Insert(L"skipped", str(Widen(r.skipped)));
    if (!r.detail.empty()) o.Insert(L"detail", str(Widen(r.detail)));
    return o;
}
