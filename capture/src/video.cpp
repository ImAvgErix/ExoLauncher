// Exo Capture - GPU device, Windows.Graphics.Capture, color conversion and the hardware video encoder.
// GPL-3.0-or-later.
#include "video.h"

#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>

#include <algorithm>

using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

// ---------------------------------------------------------------- device

std::shared_ptr<GpuDevice> GpuDevice::Create()
{
    auto gpu = std::make_shared<GpuDevice>();
    com_ptr<IDXGIFactory6> factory;
    Check(CreateDXGIFactory2(0, IID_PPV_ARGS(factory.put())), "DXGI factory");
    com_ptr<IDXGIAdapter1> adapter;
    factory->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(adapter.put()));
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    Check(D3D11CreateDevice(adapter.get(), adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
        levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, gpu->device.put(), nullptr, gpu->context.put()), "D3D11 device");
    gpu->device.as<ID3D10Multithread>()->SetMultithreadProtected(TRUE);

    auto dxgi = gpu->device.as<IDXGIDevice>();
    // The game's frames go first; a converted frame waits for a gap (each session sets its own).
    dxgi->SetGPUThreadPriority(-7);
    com_ptr<IDXGIAdapter> used;
    dxgi->GetAdapter(used.put());
    DXGI_ADAPTER_DESC desc{};
    used->GetDesc(&desc);
    gpu->luid = desc.AdapterLuid;
    gpu->adapter = desc.Description;

    UINT token = 0;
    Check(MFCreateDXGIDeviceManager(&token, gpu->manager.put()), "DXGI device manager");
    Check(gpu->manager->ResetDevice(gpu->device.get(), token), "device manager reset");

    winrt::com_ptr<::IInspectable> inspectable;
    Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()), "WinRT device");
    gpu->winrt = inspectable.as<IDirect3DDevice>();
    return gpu;
}

const wchar_t* CodecName(Codec codec)
{
    switch (codec)
    {
    case Codec::AV1: return L"AV1";
    case Codec::HEVC: return L"HEVC";
    default: return L"H.264";
    }
}

static GUID Subtype(Codec codec)
{
    switch (codec)
    {
    case Codec::AV1: return MFVideoFormat_AV1;
    case Codec::HEVC: return MFVideoFormat_HEVC;
    default: return MFVideoFormat_H264;
    }
}

namespace
{
/// The MFTs for a category and types; true and the chosen one's name when any matches the adapter.
/// One that says it is on this adapter wins over one that does not say: on a PC with two GPUs
/// (an Intel or AMD iGPU beside the gaming card) the other GPU's encoder cannot read our frames.
bool FindMft(const GUID& category, UINT32 flags, const GUID& inSub, const GUID& outSub, const LUID* luid,
    std::wstring* name, com_ptr<IMFActivate>* chosen)
{
    MFT_REGISTER_TYPE_INFO in{ MFMediaType_Video, inSub };
    MFT_REGISTER_TYPE_INFO out{ MFMediaType_Video, outSub };
    IMFActivate** list = nullptr;
    UINT32 count = 0;
    if (FAILED(MFTEnumEx(category, flags, inSub == GUID_NULL ? nullptr : &in, outSub == GUID_NULL ? nullptr : &out, &list, &count)))
        return false;
    int exact = -1, unknown = -1;
    for (UINT32 i = 0; i < count; ++i)
    {
        LUID adapter{};
        UINT32 size = 0;
        if (!luid || FAILED(list[i]->GetBlob(MFT_ENUM_ADAPTER_LUID, reinterpret_cast<UINT8*>(&adapter), sizeof(adapter), &size)))
        {
            if (unknown < 0) unknown = static_cast<int>(i);
        }
        else if (exact < 0 && adapter.LowPart == luid->LowPart && adapter.HighPart == luid->HighPart) exact = static_cast<int>(i);
    }
    int pick = exact >= 0 ? exact : unknown;
    if (pick >= 0)
    {
        if (name)
        {
            wchar_t* text = nullptr;
            UINT32 length = 0;
            if (SUCCEEDED(list[pick]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &text, &length)))
            {
                *name = text;
                CoTaskMemFree(text);
            }
        }
        if (chosen) chosen->copy_from(list[pick]);
    }
    for (UINT32 i = 0; i < count; ++i) list[i]->Release();
    CoTaskMemFree(list);
    return pick >= 0;
}
}

Codec EncoderSupport::Best() const
{
    if (av1 && av1Playback) return Codec::AV1;
    if (hevc && hevcPlayback) return Codec::HEVC;
    return Codec::H264;
}

EncoderSupport ProbeEncoders(const GpuDevice& gpu)
{
    EncoderSupport s;
    const UINT32 hw = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;
    // NVENC (GeForce), AMF (Radeon) and Quick Sync (Intel) all register here: AV1 on RTX 40, RX 7000
    // and Arc or newer, HEVC on nearly every card since 2016, H.264 on the rest.
    std::wstring names[3];
    s.h264 = FindMft(MFT_CATEGORY_VIDEO_ENCODER, hw, MFVideoFormat_NV12, MFVideoFormat_H264, &gpu.luid, &names[0], nullptr);
    s.hevc = FindMft(MFT_CATEGORY_VIDEO_ENCODER, hw, MFVideoFormat_NV12, MFVideoFormat_HEVC, &gpu.luid, &names[1], nullptr);
    s.av1 = FindMft(MFT_CATEGORY_VIDEO_ENCODER, hw, MFVideoFormat_NV12, MFVideoFormat_AV1, &gpu.luid, &names[2], nullptr);
    const UINT32 any = MFT_ENUM_FLAG_ALL;
    s.hevcPlayback = FindMft(MFT_CATEGORY_VIDEO_DECODER, any, MFVideoFormat_HEVC, GUID_NULL, nullptr, nullptr, nullptr);
    s.av1Playback = FindMft(MFT_CATEGORY_VIDEO_DECODER, any, MFVideoFormat_AV1, GUID_NULL, nullptr, nullptr, nullptr);
    Codec best = s.Best();
    s.name = names[best == Codec::AV1 ? 2 : best == Codec::HEVC ? 1 : 0];
    return s;
}

UINT BitrateFor(Codec codec, UINT width, UINT height, UINT fps, int quality)
{
    // Bits per pixel per frame from "Smaller files" (0.04) to "Best quality" (0.20).
    double bpp = 0.04 + std::clamp(quality, 0, 100) / 100.0 * 0.16;
    if (codec == Codec::H264) bpp *= 1.4;
    else if (codec == Codec::AV1) bpp *= 0.85;
    // Frames past 60 per second differ less from each other.
    double rate = std::min<double>(fps, 60) + std::max<double>(0, fps - 60.0) * 0.5;
    double bits = bpp * width * height * rate;
    return static_cast<UINT>(std::clamp(bits, 2e6, 130e6));
}

UINT RefreshRate(HMONITOR monitor)
{
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return 60;
    DEVMODEW mode{};
    mode.dmSize = sizeof(mode);
    if (!EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) || mode.dmDisplayFrequency < 24) return 60;
    return mode.dmDisplayFrequency;
}

bool MonitorIsHdr(HMONITOR monitor)
{
    com_ptr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(factory.put())))) return false;
    com_ptr<IDXGIAdapter1> adapter;
    for (UINT a = 0; factory->EnumAdapters1(a, adapter.put()) != DXGI_ERROR_NOT_FOUND; ++a, adapter = nullptr)
    {
        com_ptr<IDXGIOutput> output;
        for (UINT o = 0; adapter->EnumOutputs(o, output.put()) != DXGI_ERROR_NOT_FOUND; ++o, output = nullptr)
        {
            DXGI_OUTPUT_DESC desc{};
            output->GetDesc(&desc);
            if (desc.Monitor != monitor) continue;
            if (auto six = output.try_as<IDXGIOutput6>())
            {
                DXGI_OUTPUT_DESC1 desc1{};
                if (SUCCEEDED(six->GetDesc1(&desc1))) return desc1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            }
            return false;
        }
    }
    return false;
}

// ---------------------------------------------------------------- HDR snips

namespace
{
/// Windows' "SDR content brightness" for the monitor in scRGB (1.0 = 80 nits); 0 when unknown.
float SdrWhite(HMONITOR monitor)
{
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return 0;
    UINT32 pathCount = 0, modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS) return 0;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr) != ERROR_SUCCESS) return 0;
    for (UINT32 i = 0; i < pathCount; ++i)
    {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = sizeof(source);
        source.header.adapterId = paths[i].sourceInfo.adapterId;
        source.header.id = paths[i].sourceInfo.id;
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS || wcscmp(source.viewGdiDeviceName, info.szDevice) != 0) continue;
        DISPLAYCONFIG_SDR_WHITE_LEVEL white{};
        white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        white.header.size = sizeof(white);
        white.header.adapterId = paths[i].targetInfo.adapterId;
        white.header.id = paths[i].targetInfo.id;
        if (DisplayConfigGetDeviceInfo(&white.header) != ERROR_SUCCESS) return 0;
        return white.SDRWhiteLevel / 1000.f;
    }
    return 0;
}

float HalfToFloat(uint16_t h)
{
    uint32_t sign = (h & 0x8000u) << 16, exponent = (h >> 10) & 0x1F, mantissa = h & 0x3FF;
    uint32_t bits;
    if (exponent == 0)
    {
        if (!mantissa) bits = sign;
        else
        {
            // Subnormal: normalise it.
            int e = 1;
            while (!(mantissa & 0x400)) { mantissa <<= 1; --e; }
            mantissa &= 0x3FF;
            bits = sign | (static_cast<uint32_t>(e + 112) << 23) | (mantissa << 13);
        }
    }
    else if (exponent == 31) bits = sign | 0x7F800000u | (mantissa << 13);
    else bits = sign | ((exponent + 112) << 23) | (mantissa << 13);
    float f;
    memcpy(&f, &bits, sizeof(f));
    return f;
}
}

bool CaptureHdrMonitor(HMONITOR monitor, std::vector<uint8_t>& bgra, RECT& bounds)
{
    try
    {
        com_ptr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(factory.put())), "DXGI factory");
        com_ptr<IDXGIAdapter1> adapter;
        com_ptr<IDXGIOutput6> output;
        DXGI_OUTPUT_DESC1 desc{};
        for (UINT a = 0; !output && factory->EnumAdapters1(a, adapter.put()) != DXGI_ERROR_NOT_FOUND; ++a)
        {
            com_ptr<IDXGIOutput> candidate;
            for (UINT o = 0; adapter->EnumOutputs(o, candidate.put()) != DXGI_ERROR_NOT_FOUND; ++o, candidate = nullptr)
            {
                auto six = candidate.try_as<IDXGIOutput6>();
                if (six && SUCCEEDED(six->GetDesc1(&desc)) && desc.Monitor == monitor) { output = six; break; }
            }
            if (!output) adapter = nullptr;
        }
        // shortcut: a rotated screen keeps the plain snip; its duplicated picture would need turning first.
        if (!output || desc.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
            (desc.Rotation != DXGI_MODE_ROTATION_IDENTITY && desc.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED))
            return false;
        float white = SdrWhite(monitor);
        if (white <= 0) return false;

        com_ptr<ID3D11Device> device;
        com_ptr<ID3D11DeviceContext> context;
        Check(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
            device.put(), nullptr, context.put()), "snip device");
        // The screen as Windows composes it in HDR: linear scRGB, half floats.
        com_ptr<IDXGIOutputDuplication> duplication;
        DXGI_FORMAT formats[] = { DXGI_FORMAT_R16G16B16A16_FLOAT };
        Check(output->DuplicateOutput1(device.get(), 0, 1, formats, duplication.put()), "screen copy");
        DXGI_OUTDUPL_FRAME_INFO frame{};
        com_ptr<IDXGIResource> resource;
        Check(duplication->AcquireNextFrame(500, &frame, resource.put()), "screen frame");
        auto texture = resource.as<ID3D11Texture2D>();
        D3D11_TEXTURE2D_DESC td{};
        texture->GetDesc(&td);
        td.Usage = D3D11_USAGE_STAGING;
        td.BindFlags = 0;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        td.MiscFlags = 0;
        com_ptr<ID3D11Texture2D> staging;
        HRESULT hr = device->CreateTexture2D(&td, nullptr, staging.put());
        if (SUCCEEDED(hr)) context->CopyResource(staging.get(), texture.get());
        duplication->ReleaseFrame();
        Check(hr, "screen copy texture");
        bounds = desc.DesktopCoordinates;
        UINT w = td.Width, h = td.Height;
        if (td.Format != DXGI_FORMAT_R16G16B16A16_FLOAT || static_cast<LONG>(w) != bounds.right - bounds.left ||
            static_cast<LONG>(h) != bounds.bottom - bounds.top)
            return false;
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped), "screen read");

        // Linear light where 1.0 is SDR white: the desktop and SDR apps come out as they look.
        std::vector<float> linear(static_cast<size_t>(w) * h * 3);
        float peak = 0;
        for (UINT y = 0; y < h; ++y)
        {
            auto* row = reinterpret_cast<const uint16_t*>(static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(mapped.RowPitch) * y);
            float* out = linear.data() + static_cast<size_t>(y) * w * 3;
            for (UINT x = 0; x < w; ++x)
                for (int c = 0; c < 3; ++c)
                {
                    float v = std::max(0.f, HalfToFloat(row[x * 4 + c]) / white);
                    if (!(v < 1e4f)) v = 0; // NaN or infinity
                    out[x * 3 + c] = v;
                    peak = std::max(peak, v);
                }
        }
        context->Unmap(staging.get(), 0);

        // Brighter than SDR white (an HDR game or video): roll the highlights off above a knee so the
        // brightest pixel lands on white, instead of clipping them flat. Hue holds: all three channels scale.
        const float knee = 0.75f;
        const float range = (peak - knee) / (1 - knee);
        auto toneMap = [&](float m) {
            float u = (m - knee) / (1 - knee);
            return knee + (1 - knee) * u * (1 + u / (range * range)) / (1 + u);
        };
        // sRGB encoding through a table: a 4K screen is 25 million channels.
        static const std::vector<uint8_t> encode = [] {
            std::vector<uint8_t> t(16384);
            for (size_t i = 0; i < t.size(); ++i)
            {
                double v = static_cast<double>(i) / (t.size() - 1);
                double s = v <= 0.0031308 ? 12.92 * v : 1.055 * std::pow(v, 1 / 2.4) - 0.055;
                t[i] = static_cast<uint8_t>(std::clamp(s * 255 + 0.5, 0.0, 255.0));
            }
            return t;
        }();
        auto q = [&](float v) { return encode[static_cast<size_t>(std::clamp(v, 0.f, 1.f) * (encode.size() - 1) + 0.5f)]; };
        bgra.resize(static_cast<size_t>(w) * h * 4);
        for (size_t p = 0, n = static_cast<size_t>(w) * h; p < n; ++p)
        {
            float r = linear[p * 3], g = linear[p * 3 + 1], b = linear[p * 3 + 2];
            float m = std::max({ r, g, b });
            if (peak > 1.001f && m > knee)
            {
                float k = toneMap(m) / m;
                r *= k; g *= k; b *= k;
            }
            bgra[p * 4] = q(b);
            bgra[p * 4 + 1] = q(g);
            bgra[p * 4 + 2] = q(r);
            bgra[p * 4 + 3] = 255;
        }
        return true;
    }
    catch (const std::exception& e)
    {
        Log(std::string("HDR snip: ") + e.what() + "; the plain one is kept.");
        return false;
    }
    catch (winrt::hresult_error const& e)
    {
        Log("HDR snip: " + Narrow(std::wstring(e.message())) + "; the plain one is kept.");
        return false;
    }
}

// ---------------------------------------------------------------- encoder

namespace
{
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

void SetApi(ICodecAPI* api, const GUID& key, UINT32 value)
{
    VARIANT v{};
    v.vt = VT_UI4;
    v.ulVal = value;
    api->SetValue(&key, &v);
}

void SetApiBool(ICodecAPI* api, const GUID& key, bool value)
{
    VARIANT v{};
    v.vt = VT_BOOL;
    v.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
    api->SetValue(&key, &v);
}

}

std::vector<uint8_t> ParameterSets(const std::vector<uint8_t>& data, Codec codec)
{
    std::vector<uint8_t> out;
    size_t i = 0, n = data.size();
    auto startCode = [&](size_t at, size_t& length) {
        if (at + 3 <= n && data[at] == 0 && data[at + 1] == 0 && data[at + 2] == 1) { length = 3; return true; }
        if (at + 4 <= n && data[at] == 0 && data[at + 1] == 0 && data[at + 2] == 0 && data[at + 3] == 1) { length = 4; return true; }
        return false;
    };
    size_t sc = 0;
    while (i < n && !startCode(i, sc)) ++i;
    while (i < n)
    {
        size_t begin = i + sc;
        size_t j = begin;
        while (j < n && !startCode(j, sc)) ++j;
        if (begin < j)
        {
            uint8_t header = data[begin];
            bool keep = codec == Codec::HEVC
                ? ((header >> 1) & 0x3F) >= 32 && ((header >> 1) & 0x3F) <= 34
                : (header & 0x1F) == 7 || (header & 0x1F) == 8;
            if (keep)
            {
                static const uint8_t code[] = { 0, 0, 0, 1 };
                out.insert(out.end(), code, code + 4);
                out.insert(out.end(), data.begin() + begin, data.begin() + j);
            }
        }
        i = j;
    }
    return out;
}

VideoEncoder::VideoEncoder(GpuDevice& gpu, Codec codec, UINT width, UINT height, UINT fps, UINT bitrate, bool hdr, bool realtime,
    std::function<void(PacketPtr)> sink, bool software)
    : m_gpu(gpu), m_codec(codec), m_fps(fps), m_width(width), m_height(height), m_sink(std::move(sink)), m_sync(software)
{
    const GUID input = hdr ? MFVideoFormat_P010 : MFVideoFormat_NV12;
    com_ptr<IMFActivate> activate;
    if (software)
    {
        if (!FindMft(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, input, Subtype(codec),
            nullptr, &m_name, &activate))
            throw Failure(MF_E_TOPO_CODEC_NOT_FOUND, "no software video encoder");
    }
    else if (!FindMft(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, input, Subtype(codec),
        &gpu.luid, &m_name, &activate) &&
        !FindMft(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, input, Subtype(codec),
        nullptr, &m_name, &activate))
        throw Failure(MF_E_TOPO_CODEC_NOT_FOUND, "no hardware video encoder");
    Check(activate->ActivateObject(IID_PPV_ARGS(m_mft.put())), "start the video encoder");

    com_ptr<IMFAttributes> attrs;
    if (SUCCEEDED(m_mft->GetAttributes(attrs.put())))
    {
        attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
        attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
    }
    if (!m_sync) Check(m_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(gpu.manager.get())), "encoder GPU");
    if (m_mft->GetStreamIDs(1, &m_in, 1, &m_out) != S_OK) { m_in = 0; m_out = 0; }

    m_api = m_mft.as<ICodecAPI>();
    if (m_api)
    {
        VARIANT mode{};
        mode.vt = VT_UI4;
        if (realtime)
        {
            // A live stream: the rate the network was promised, with half a second of buffer so a key
            // frame or a scene cut fits in, and a quality floor. Without the floor a still screen drives
            // the quantizer to its finest, and the first moving frames after it come out several times
            // over the rate (a burst, then a dip while the encoder pays it back).
            mode.ulVal = eAVEncCommonRateControlMode_CBR;
            if (FAILED(m_api->SetValue(&CODECAPI_AVEncCommonRateControlMode, &mode)))
                SetApi(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_PeakConstrainedVBR);
            SetApi(m_api.get(), CODECAPI_AVEncCommonBufferSize, bitrate / 2);
            SetApi(m_api.get(), CODECAPI_AVEncVideoMinQP, 18);
        }
        else
        {
            mode.ulVal = eAVEncCommonRateControlMode_PeakConstrainedVBR;
            if (FAILED(m_api->SetValue(&CODECAPI_AVEncCommonRateControlMode, &mode)))
            {
                mode.ulVal = eAVEncCommonRateControlMode_UnconstrainedVBR;
                if (FAILED(m_api->SetValue(&CODECAPI_AVEncCommonRateControlMode, &mode)))
                    SetApi(m_api.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
            }
        }
        SetApi(m_api.get(), CODECAPI_AVEncCommonMeanBitRate, bitrate);
        SetApi(m_api.get(), CODECAPI_AVEncCommonMaxBitRate, static_cast<UINT32>(std::min<double>(bitrate * (realtime ? 1.2 : 1.5), 200e6)));
        // A stream's key frames come when viewers ask (one joins, or loses a packet resends can't bring back), and
        // otherwise once a minute, as libwebrtc's senders keep them: one every five seconds is six to nine frames' worth
        // sent at once, and every viewer saw it as a hitch.
        SetApi(m_api.get(), CODECAPI_AVEncMPVGOPSize, fps * (realtime ? 60 : 2));
        SetApi(m_api.get(), CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        SetApiBool(m_api.get(), CODECAPI_AVLowLatencyMode, true);
        // Leans to speed: NVENC, AMF and Quick Sync run beside the game's own GPU work. A stream's
        // bit rate is lower than a file's, so it takes a little more care per frame.
        SetApi(m_api.get(), CODECAPI_AVEncCommonQualityVsSpeed, realtime ? 55 : 40);
    }

    com_ptr<IMFMediaType> out;
    Check(MFCreateMediaType(out.put()), "media type");
    out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    out->SetGUID(MF_MT_SUBTYPE, Subtype(codec));
    out->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    MFSetAttributeSize(out.get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(out.get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(out.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    out->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    out->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
    if (hdr)
    {
        out->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT2020);
        out->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_2084);
        out->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT2020_10);
        out->SetUINT32(MF_MT_MPEG2_PROFILE, codec == Codec::AV1 ? eAVEncAV1VProfile_Main_420_10 : eAVEncH265VProfile_Main_420_10);
    }
    else
    {
        out->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
        out->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
        out->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
        out->SetUINT32(MF_MT_MPEG2_PROFILE, codec == Codec::AV1 ? eAVEncAV1VProfile_Main_420_8
            : codec == Codec::HEVC ? eAVEncH265VProfile_Main_420_8 : eAVEncH264VProfile_High);
    }
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
    MFSetAttributeSize(in.get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(in.get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(in.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    Check(m_mft->SetInputType(m_in, in.get(), 0), "encoder input type");

    if (!m_sync) m_events = m_mft.as<IMFMediaEventGenerator>();
    m_mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    Check(m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "encoder start");
    Check(m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "encoder stream start");
    if (!m_sync) m_thread = std::thread([this] { EventLoop(); });
    else Log("Encoder: " + Narrow(m_name) + " on the CPU (the graphics card's would not start)");
}

VideoEncoder::~VideoEncoder()
{
    {
        std::lock_guard lock(m_lock);
        m_queue.clear();
    }
    m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, m_in);
    m_mft->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, m_in);
    if (!m_sync)
    {
        std::unique_lock lock(m_lock);
        m_changed.wait_for(lock, std::chrono::seconds(2), [this] { return m_drained; });
    }
    if (auto shutdown = m_mft.try_as<IMFShutdown>()) shutdown->Shutdown();
    if (m_thread.joinable()) m_thread.join();
    if (!m_sync) m_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, 0);
}

void VideoEncoder::Submit(ID3D11Texture2D* texture, int64_t time, std::function<void()> released)
{
    if (m_sync)
    {
        try { SubmitSync(texture, time); }
        catch (const std::exception& e) { Log(std::string("CPU encoder: ") + e.what()); }
        if (released) released();
        return;
    }
    com_ptr<IMFMediaBuffer> buffer;
    Check(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), texture, 0, FALSE, buffer.put()), "frame buffer");
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
    sample->SetSampleDuration(10'000'000 / m_fps);
    auto callback = winrt::make<ReleaseCallback>(std::move(released));
    tracked->SetAllocator(callback.as<IMFAsyncCallback>().get(), nullptr);

    std::lock_guard lock(m_lock);
    m_queue.push_back(sample);
    // The encoder fell behind: drop the oldest waiting frame rather than grow.
    while (m_queue.size() > 3) m_queue.pop_front();
    FeedLocked();
}

// The CPU encoder takes the frame from memory: copied off the GPU (NV12, its chroma plane below the luma rows), encoded
// on this thread, and every packet it has ready taken at once.
void VideoEncoder::SubmitSync(ID3D11Texture2D* texture, int64_t time)
{
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    if (!m_staging)
    {
        auto staging = desc;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        staging.MiscFlags = 0;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        Check(m_gpu.device->CreateTexture2D(&staging, nullptr, m_staging.put()), "frame copy for the CPU encoder");
    }
    m_gpu.context->CopyResource(m_staging.get(), texture);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(m_gpu.context->Map(m_staging.get(), 0, D3D11_MAP_READ, 0, &mapped), "frame read for the CPU encoder");
    const UINT w = m_width, h = m_height, size = w * h * 3 / 2;
    com_ptr<IMFMediaBuffer> buffer;
    HRESULT made = MFCreateMemoryBuffer(size, buffer.put());
    BYTE* out = nullptr;
    if (SUCCEEDED(made) && SUCCEEDED(buffer->Lock(&out, nullptr, nullptr)))
    {
        auto src = static_cast<const BYTE*>(mapped.pData);
        for (UINT y = 0; y < h; ++y) memcpy(out + static_cast<size_t>(y) * w, src + static_cast<size_t>(y) * mapped.RowPitch, w);
        auto uv = src + static_cast<size_t>(mapped.RowPitch) * desc.Height;
        for (UINT y = 0; y < h / 2; ++y)
            memcpy(out + static_cast<size_t>(w) * h + static_cast<size_t>(y) * w, uv + static_cast<size_t>(y) * mapped.RowPitch, w);
        buffer->Unlock();
        buffer->SetCurrentLength(size);
    }
    m_gpu.context->Unmap(m_staging.get(), 0);
    Check(made, "frame buffer");
    com_ptr<IMFSample> sample;
    Check(MFCreateSample(sample.put()), "frame sample");
    sample->AddBuffer(buffer.get());
    sample->SetSampleTime(time);
    sample->SetSampleDuration(10'000'000 / m_fps);
    TakeRequests();
    HRESULT hr = m_mft->ProcessInput(m_in, sample.get(), 0);
    if (hr == MF_E_NOTACCEPTING)
    {
        while (TakeOutput()) {}
        hr = m_mft->ProcessInput(m_in, sample.get(), 0);
    }
    if (FAILED(hr)) Log("CPU encoder refused a frame: " + std::string(Failure(hr, "ProcessInput").what()));
    while (TakeOutput()) {}
}

// What was asked since the last frame, applied before the next goes in: a key frame, a new bit rate (the
// hardware encoders take a mean and peak rate mid-stream; the buffer stays as it started).
void VideoEncoder::TakeRequests()
{
    if (!m_api) return;
    if (m_forceKey.exchange(false)) SetApi(m_api.get(), CODECAPI_AVEncVideoForceKeyFrame, 1);
    if (UINT rate = m_newRate.exchange(0))
    {
        SetApi(m_api.get(), CODECAPI_AVEncCommonMeanBitRate, rate);
        SetApi(m_api.get(), CODECAPI_AVEncCommonMaxBitRate, static_cast<UINT32>(std::min<double>(rate * 1.2, 200e6)));
    }
}

void VideoEncoder::FeedLocked()
{
    while (m_credits > 0 && !m_queue.empty())
    {
        auto sample = m_queue.front();
        TakeRequests();
        HRESULT hr = m_mft->ProcessInput(m_in, sample.get(), 0);
        if (hr == MF_E_NOTACCEPTING) break;
        m_queue.pop_front();
        --m_credits;
        if (FAILED(hr))
        {
            Log("Encoder refused a frame: " + std::string(Failure(hr, "ProcessInput").what()));
            break;
        }
    }
}

void VideoEncoder::EventLoop()
{
    for (;;)
    {
        com_ptr<IMFMediaEvent> event;
        if (FAILED(m_events->GetEvent(0, event.put()))) break;
        MediaEventType type = MEUnknown;
        event->GetType(&type);
        if (type == METransformNeedInput)
        {
            std::lock_guard lock(m_lock);
            ++m_credits;
            FeedLocked();
        }
        else if (type == METransformHaveOutput)
        {
            try { TakeOutput(); }
            catch (const std::exception& e) { Log(std::string("Encoder output: ") + e.what()); }
        }
        else if (type == METransformDrainComplete)
        {
            std::lock_guard lock(m_lock);
            m_drained = true;
            m_changed.notify_all();
        }
    }
}

bool VideoEncoder::TakeOutput()
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
        return true;
    }
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
    Check(hr, "encode");
    if (!sample) return false;

    auto packet = std::make_shared<Packet>();
    packet->track = 0;
    LONGLONG time = 0, duration = 0;
    sample->GetSampleTime(&time);
    sample->GetSampleDuration(&duration);
    packet->time = time;
    packet->duration = duration;
    packet->key = MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, FALSE) != FALSE;
    com_ptr<IMFMediaBuffer> buffer;
    Check(sample->ConvertToContiguousBuffer(buffer.put()), "encoded data");
    BYTE* bytes = nullptr;
    DWORD length = 0;
    Check(buffer->Lock(&bytes, nullptr, &length), "encoded lock");
    packet->data.assign(bytes, bytes + length);
    buffer->Unlock();

    bool needType = false;
    {
        std::lock_guard lock(m_lock);
        needType = !m_outputType && packet->key;
    }
    if (needType)
    {
        com_ptr<IMFMediaType> current, type;
        Check(m_mft->GetOutputCurrentType(m_out, current.put()), "encoder type");
        Check(MFCreateMediaType(type.put()), "media type");
        current->CopyAllItems(type.get());
        // The file states the picture's own size; the encoder reports its padded block size.
        MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, m_width, m_height);
        type->DeleteItem(MF_MT_MINIMUM_DISPLAY_APERTURE);
        type->DeleteItem(MF_MT_GEOMETRIC_APERTURE);
        UINT32 headerSize = 0;
        if (FAILED(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerSize)) || headerSize == 0)
        {
            auto sets = ParameterSets(packet->data, m_codec);
            if (!sets.empty()) type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, sets.data(), static_cast<UINT32>(sets.size()));
        }
        std::lock_guard lock(m_lock);
        m_outputType = type;
        m_changed.notify_all();
    }
    if (m_sink) m_sink(packet);
    return true;
}

com_ptr<IMFMediaType> VideoEncoder::OutputType()
{
    std::unique_lock lock(m_lock);
    m_changed.wait_for(lock, std::chrono::seconds(3), [this] { return m_outputType != nullptr; });
    return m_outputType;
}

// ---------------------------------------------------------------- capture

namespace
{
GraphicsCaptureItem ItemFor(HWND window, HMONITOR monitor)
{
    auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{ nullptr };
    if (window)
        Check(interop->CreateForWindow(window, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)), "capture the window");
    else
        Check(interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)), "capture the screen");
    return item;
}

/// Windows draws a glowing outline around anything being captured, and it lands in the picture.
/// Turning it off takes borderless access, asked once (no prompt for a desktop app).
bool Borderless()
{
    static const bool allowed = [] {
        try { return GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless).get() == winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus::Allowed; }
        catch (...) { return false; }
    }();
    return allowed;
}

/// The frame's picture, copied to memory the processor reads, as a JPEG.
std::vector<uint8_t> FrameJpeg(GpuDevice& gpu, Direct3D11CaptureFrame const& frame, UINT maxWidth)
{
    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    com_ptr<ID3D11Texture2D> texture;
    Check(access->GetInterface(IID_PPV_ARGS(texture.put())), "frame texture");
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    auto content = frame.ContentSize();
    UINT w = std::min<UINT>(std::max(content.Width, 1), desc.Width);
    UINT h = std::min<UINT>(std::max(content.Height, 1), desc.Height);
    desc.Width = w;
    desc.Height = h;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    com_ptr<ID3D11Texture2D> staging;
    Check(gpu.device->CreateTexture2D(&desc, nullptr, staging.put()), "picture copy");
    D3D11_BOX box{ 0, 0, 0, w, h, 1 };
    gpu.context->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, texture.get(), 0, &box);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(gpu.context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped), "picture read");
    std::vector<uint8_t> jpeg;
    try { jpeg = EncodeJpeg(static_cast<const uint8_t*>(mapped.pData), w, h, mapped.RowPitch, maxWidth); }
    catch (...) { gpu.context->Unmap(staging.get(), 0); throw; }
    gpu.context->Unmap(staging.get(), 0);
    return jpeg;
}
}

std::vector<uint8_t> GrabFrame(GpuDevice& gpu, HWND window, HMONITOR monitor, UINT maxWidth)
{
    auto item = ItemFor(window, monitor);
    auto size = item.Size();
    if (size.Width <= 0 || size.Height <= 0) return {};
    auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(gpu.winrt, DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size);
    auto session = pool.CreateCaptureSession(item);
    try { session.IsCursorCaptureEnabled(false); } catch (...) {}
    if (Borderless()) { try { session.IsBorderRequired(false); } catch (...) {} }
    // Shared with the handler, which may still be running when this returns.
    auto ready = std::make_shared<winrt::handle>(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    auto token = pool.FrameArrived([ready](auto&&, auto&&) { SetEvent(ready->get()); });
    auto close = [&] {
        pool.FrameArrived(token);
        try { session.Close(); pool.Close(); } catch (...) {}
    };
    std::vector<uint8_t> jpeg;
    try
    {
        session.StartCapture();
        if (WaitForSingleObject(ready->get(), 1000) == WAIT_OBJECT_0)
            if (auto frame = pool.TryGetNextFrame()) jpeg = FrameJpeg(gpu, frame, maxWidth);
    }
    catch (...) { close(); throw; }
    close();
    return jpeg;
}

CaptureSession::CaptureSession(std::shared_ptr<GpuDevice> gpu, const Options& options, std::function<void(PacketPtr)> sink)
    : m_gpu(std::move(gpu)), m_options(options), m_codec(options.codec)
{
    m_fps = std::clamp<UINT>(options.fps, 10, 120);
    m_interval = 10'000'000 / m_fps;
    m_keyInterval = options.realtime ? 50'000'000 : 10'000'000;
    HMONITOR monitor = options.window ? MonitorFromWindow(options.window, MONITOR_DEFAULTTONEAREST) : options.monitor;
    // A live stream stays SDR: Discord's viewers decode 8-bit pictures, whatever the screen.
    m_hdr = !options.realtime && m_codec != Codec::H264 && MonitorIsHdr(monitor);
    winrt::Windows::Graphics::SizeInt32 size{};
    bool wide = true; // false: the frames from inside the game are SDR
    if (options.window && options.gameHook)
    {
        try { m_hook = GameHook::Start(options.window, 1'000'000'000ull / m_fps, m_gpu->device.get()); }
        catch (const std::exception& e) { Log(std::string("Game hook: ") + e.what()); }
    }
    if (m_hook)
    {
        size = { m_hook->Size().cx, m_hook->Size().cy };
        // An SDR game on an HDR screen stays SDR: its frames are what the game drew.
        D3D11_TEXTURE2D_DESC desc{};
        if (auto frame = m_hook->Frame()) frame->GetDesc(&desc);
        wide = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT || desc.Format == DXGI_FORMAT_R16G16B16A16_TYPELESS ||
            desc.Format == DXGI_FORMAT_R10G10B10A2_UNORM || desc.Format == DXGI_FORMAT_R10G10B10A2_TYPELESS;
        m_hdr = m_hdr && wide;
    }
    else
    {
        m_item = ItemFor(options.window, options.monitor);
        size = m_item.Size();
    }

    // Encode at the game's own size, within what the encoder takes.
    double w = std::max(64, size.Width), h = std::max(64, size.Height);
    double maxSide = m_codec == Codec::H264 ? 4096 : 8192;
    double scale = std::min({ 1.0, maxSide / w, maxSide / h, std::sqrt(3840.0 * 2160.0 / (w * h)) });
    m_width = static_cast<UINT>(w * scale) & ~1u;
    m_height = static_cast<UINT>(h * scale) & ~1u;
    // Go Live asks for its own size (the content is fitted in, black around it if the shape differs).
    if (options.width >= 64 && options.height >= 64)
    {
        m_width = std::min<UINT>(options.width, static_cast<UINT>(maxSide)) & ~1u;
        m_height = std::min<UINT>(options.height, static_cast<UINT>(maxSide)) & ~1u;
    }
    m_bitrate = options.bitrate ? options.bitrate : BitrateFor(m_codec, m_width, m_height, m_fps, options.quality);

    m_videoDevice = m_gpu->device.as<ID3D11VideoDevice>();
    m_videoContext = m_gpu->context.as<ID3D11VideoContext>();

    // A driver can list an encoder that then refuses this size or HDR: step down, HDR before SDR,
    // AV1 to HEVC to H.264 (which carries no HDR), then H.264 at 60 and 30 fps and at most 1080p
    // (an encoder's limit is pixels a second, and some cards' is lower than the screen's), until one starts.
    UINT rate = options.bitrate;
    // A check of the CPU encoder on a PC whose graphics card's works (EXO_CAPTURE_CPU in Exo's environment): Go Live
    // starts on it, as it would after the card's refused.
    if (options.realtime && GetEnvironmentVariableW(L"EXO_CAPTURE_CPU", nullptr, 0) > 0)
    {
        m_software = true;
        m_fps = std::min<UINT>(m_fps, 30);
        double f = std::min({ 1.0, 1280.0 / std::max(m_width, m_height), 720.0 / std::min(m_width, m_height) });
        m_width = static_cast<UINT>(m_width * f) & ~1u;
        m_height = static_cast<UINT>(m_height * f) & ~1u;
        m_interval = 10'000'000 / m_fps;
    }
    for (;;)
    {
        try
        {
            m_bitrate = rate ? rate : BitrateFor(m_codec, m_width, m_height, m_fps, options.quality);
            m_encoder = std::make_unique<VideoEncoder>(*m_gpu, m_codec, m_width, m_height, m_fps, m_bitrate, m_hdr, options.realtime, sink, m_software);
            break;
        }
        catch (const std::exception& e)
        {
            Log(std::string("Encoder ") + Narrow(CodecName(m_codec)) + (m_hdr ? " HDR " : " ") + std::to_string(m_width) + "x" +
                std::to_string(m_height) + " @" + std::to_string(m_fps) + " failed: " + e.what());
            if (m_hdr) m_hdr = false;
            else if (m_codec == Codec::H264)
            {
                // No encoder at all is not a matter of settings.
                auto failure = dynamic_cast<const Failure*>(&e);
                bool none = failure && failure->hr == MF_E_TOPO_CODEC_NOT_FOUND;
                double before = static_cast<double>(m_width) * m_height * m_fps;
                auto fit = [this](double w, double h) {
                    double f = std::min(w / std::max(m_width, m_height), h / std::min(m_width, m_height));
                    m_width = static_cast<UINT>(m_width * f) & ~1u;
                    m_height = static_cast<UINT>(m_height * f) & ~1u;
                };
                if (!m_software && !none && m_fps > 60) m_fps = 60;
                else if (!m_software && !none && m_fps > 30) m_fps = 30;
                else if (!m_software && !none && static_cast<double>(m_width) * m_height > 1920.0 * 1080.0) fit(1920.0, 1080.0);
                else if (!m_software && options.realtime)
                {
                    // The graphics card's encoder won't start at all (another GPU's, none, or refused): Go Live on
                    // Windows' own H.264 encoder, on the CPU, at most 720p 30 so it keeps up, rather than not at all.
                    m_software = true;
                    m_fps = std::min<UINT>(m_fps, 30);
                    if (static_cast<double>(m_width) * m_height > 1280.0 * 720.0) fit(1280.0, 720.0);
                }
                else throw;
                m_interval = 10'000'000 / m_fps;
                // A rate asked for (Go Live's) was for the bigger picture: it scales down with it.
                if (rate) rate = static_cast<UINT>(std::max(1e6, rate * static_cast<double>(m_width) * m_height * m_fps / before));
            }
            else
            {
                m_codec = m_codec == Codec::AV1 && options.hevcFallback ? Codec::HEVC : Codec::H264;
                m_hdr = !options.realtime && m_codec != Codec::H264 && MonitorIsHdr(monitor) && wide;
            }
        }
    }

    DXGI_FORMAT targetFormat = m_hdr ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
    for (int i = 0; i < 6; ++i)
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = m_width;
        desc.Height = m_height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = targetFormat;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        Target target;
        Check(m_gpu->device->CreateTexture2D(&desc, nullptr, target.texture.put()), "frame texture");
        m_targets.push_back(std::move(target));
        m_free->push_back(i);
    }

    // Through Windows, the converter waits its turn behind the game. From inside the game it reads
    // a texture the game writes next, so at low priority it would hold the game up.
    m_gpu->device.as<IDXGIDevice>()->SetGPUThreadPriority(m_hook ? 0 : -7);
    if (m_hook) m_pump = std::thread([this] { Pump(); });
    else StartWindows(options);
    m_repeat = std::thread([this] { Repeat(); });
}

void CaptureSession::StartWindows(const Options& options)
{
    auto size = m_item.Size();
    auto pixelFormat = m_hdr ? DirectXPixelFormat::R16G16B16A16Float : DirectXPixelFormat::B8G8R8A8UIntNormalized;
    m_pool = Direct3D11CaptureFramePool::CreateFreeThreaded(m_gpu->winrt, pixelFormat, 2, size);
    m_poolSize = size;
    m_session = m_pool.CreateCaptureSession(m_item);
    try { m_session.IsCursorCaptureEnabled(options.cursor); } catch (...) {}
    if (Borderless()) { try { m_session.IsBorderRequired(false); } catch (...) {} }
    // Windows hands over no more frames than the clip keeps: a game drawing faster costs nothing extra.
    // A stream asks for twice its rate, so every tick of its clock finds a picture newer than the last.
    const int64_t every = options.realtime ? m_interval / 2 : m_interval * 9 / 10;
    try { m_session.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan(every)); } catch (...) {}
    m_item.Closed([this](auto&&, auto&&) { m_closed = true; });
    m_pool.FrameArrived([this](Direct3D11CaptureFramePool const& pool, auto&&) {
        try { OnFrame(pool); }
        catch (const std::exception& e) { Log(std::string("Frame: ") + e.what()); }
        catch (winrt::hresult_error const& e) { Log("Frame: " + Narrow(std::wstring(e.message()))); }
    });
    m_session.StartCapture();
}

void CaptureSession::Pump()
{
    // The hook sends no signal per frame: take the newest one on the clip's own clock (twice as
    // often for a stream, whose own clock then picks the newest).
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    const int64_t step = m_options.realtime ? m_interval / 2 : m_interval;
    int64_t next = Now100ns();
    while (!m_stop)
    {
        next += step;
        int64_t wait = next - Now100ns();
        if (wait < -m_interval) { next = Now100ns(); wait = 0; }
        if (wait > 0)
        {
            LARGE_INTEGER due{};
            due.QuadPart = -wait;
            if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 1000);
            else Sleep(static_cast<DWORD>(wait / 10'000));
        }
        if (m_stop) break;
        try
        {
            std::lock_guard lock(m_convert);
            if (m_closed) break;
            auto frame = m_hook->Frame();
            if (!frame) { m_closed = true; break; }
            D3D11_TEXTURE2D_DESC desc{};
            frame->GetDesc(&desc);
            EnsureProcessor(desc.Width, desc.Height, desc.Format);
            m_content = { static_cast<LONG>(desc.Width), static_cast<LONG>(desc.Height) };
            Convert(Now100ns(), InputFor(frame, desc.Width, desc.Height));
        }
        catch (const std::exception& e) { Log(std::string("Game frame: ") + e.what()); }
    }
    if (timer) CloseHandle(timer);
}

CaptureSession::~CaptureSession()
{
    m_stop = true;
    if (m_repeat.joinable()) m_repeat.join();
    if (m_pump.joinable()) m_pump.join();
    {
        std::lock_guard lock(m_convert);
        m_closed = true;
        if (m_session) { try { m_session.Close(); } catch (...) {} }
        if (m_pool) { try { m_pool.Close(); } catch (...) {} }
        m_inputs.clear();
        m_copy = nullptr;
        m_copyView = nullptr;
        m_hook.reset();
    }
    m_encoder.reset();
}

SIZE CaptureSession::ContentSize() const
{
    std::lock_guard lock(const_cast<std::mutex&>(m_convert));
    return m_content;
}

void CaptureSession::OnFrame(Direct3D11CaptureFramePool const& pool)
{
    auto frame = pool.TryGetNextFrame();
    if (!frame || m_stop) return;
    ++m_arrived;
    auto size = frame.ContentSize();
    if (size.Width != m_poolSize.Width || size.Height != m_poolSize.Height)
    {
        {
            std::lock_guard lock(m_convert);
            m_inputs.clear();
        }
        m_poolSize = size;
        pool.Recreate(m_gpu->winrt, m_hdr ? DirectXPixelFormat::R16G16B16A16Float : DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
    }
    int64_t time = frame.SystemRelativeTime().count();
    // Hold the frame rate: the game may draw faster than the clip records. A stream takes every
    // picture; its clock picks the newest (Repeat).
    if (!m_options.realtime)
    {
        if (m_lastFrame && time < m_lastFrame - m_interval / 8) return;
        if (time - m_lastFrame > m_interval) m_lastFrame = time;
        m_lastFrame += m_interval;
    }

    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    com_ptr<ID3D11Texture2D> texture;
    Check(access->GetInterface(IID_PPV_ARGS(texture.put())), "frame texture");
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    UINT cw = std::min<UINT>(std::max(size.Width, 1), desc.Width);
    UINT ch = std::min<UINT>(std::max(size.Height, 1), desc.Height);

    std::lock_guard lock(m_convert);
    if (m_closed) return;
    EnsureProcessor(desc.Width, desc.Height, desc.Format);
    m_content = { static_cast<LONG>(cw), static_cast<LONG>(ch) };
    Convert(time, InputFor(texture.get(), cw, ch));
}

/// A format the converter reads, of the same family (game frames may be typeless or sRGB).
static DXGI_FORMAT TypedFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_B8G8R8X8_TYPELESS: case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8X8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return format;
    }
}

ID3D11VideoProcessorInputView* CaptureSession::InputFor(ID3D11Texture2D* texture, UINT width, UINT height)
{
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc{};
    inDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    if (m_direct)
    {
        // The pool reuses two or three surfaces: one view each, made once.
        for (auto& input : m_inputs)
            if (input.texture.get() == texture) return input.view.get();
        Input input;
        input.texture.copy_from(texture);
        if (SUCCEEDED(m_videoDevice->CreateVideoProcessorInputView(texture, m_vpEnum.get(), &inDesc, input.view.put())))
        {
            if (m_inputs.size() >= 4) m_inputs.erase(m_inputs.begin());
            m_inputs.push_back(std::move(input));
            return m_inputs.back().view.get();
        }
        Log("The converter cannot read capture surfaces here; copying each frame.");
        m_direct = false;
        m_inputs.clear();
    }
    if (!m_copy)
    {
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        desc.Format = TypedFormat(desc.Format);
        desc.Width = m_inW;
        desc.Height = m_inH;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;
        Check(m_gpu->device->CreateTexture2D(&desc, nullptr, m_copy.put()), "frame copy");
        Check(m_videoDevice->CreateVideoProcessorInputView(m_copy.get(), m_vpEnum.get(), &inDesc, m_copyView.put()), "converter input");
    }
    D3D11_BOX box{ 0, 0, 0, width, height, 1 };
    m_gpu->context->CopySubresourceRegion(m_copy.get(), 0, 0, 0, 0, texture, 0, &box);
    return m_copyView.get();
}

void CaptureSession::EnsureProcessor(UINT width, UINT height, DXGI_FORMAT format)
{
    if (m_vp && m_inW == width && m_inH == height && m_inFormat == format) return;
    m_inputs.clear();
    m_copy = nullptr;
    m_copyView = nullptr;
    m_vp = nullptr;
    m_vpEnum = nullptr;
    for (auto& t : m_targets) t.view = nullptr;
    m_inW = width;
    m_inH = height;
    m_inFormat = format;

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
    content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputWidth = width;
    content.InputHeight = height;
    content.OutputWidth = m_width;
    content.OutputHeight = m_height;
    content.InputFrameRate = { m_fps, 1 };
    content.OutputFrameRate = { m_fps, 1 };
    content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    Check(m_videoDevice->CreateVideoProcessorEnumerator(&content, m_vpEnum.put()), "color converter");
    Check(m_videoDevice->CreateVideoProcessor(m_vpEnum.get(), 0, m_vp.put()), "color converter");

    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc{};
    outDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    for (auto& t : m_targets)
        Check(m_videoDevice->CreateVideoProcessorOutputView(t.texture.get(), m_vpEnum.get(), &outDesc, t.view.put()), "converter output");

    if (auto context1 = m_videoContext.try_as<ID3D11VideoContext1>())
    {
        auto typed = TypedFormat(format);
        auto input = typed == DXGI_FORMAT_R16G16B16A16_FLOAT ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
            : typed == DXGI_FORMAT_R10G10B10A2_UNORM && m_hdr ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
            : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        context1->VideoProcessorSetStreamColorSpace1(m_vp.get(), 0, input);
        context1->VideoProcessorSetOutputColorSpace1(m_vp.get(),
            m_hdr ? DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020 : DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
    }
    else
    {
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE in{}, out{};
        in.RGB_Range = 0;
        out.YCbCr_Matrix = 1;
        out.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        m_videoContext->VideoProcessorSetStreamColorSpace(m_vp.get(), 0, &in);
        m_videoContext->VideoProcessorSetOutputColorSpace(m_vp.get(), &out);
    }
    m_videoContext->VideoProcessorSetStreamFrameFormat(m_vp.get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    m_videoContext->VideoProcessorSetStreamAutoProcessingMode(m_vp.get(), 0, FALSE);
    D3D11_VIDEO_COLOR black{};
    black.RGBA = { 0, 0, 0, 1 };
    m_videoContext->VideoProcessorSetOutputBackgroundColor(m_vp.get(), FALSE, &black);
}

void CaptureSession::Convert(int64_t time, ID3D11VideoProcessorInputView* input)
{
    if (!m_vp || !input || m_content.cx <= 0) return;
    int index = -1;
    {
        std::lock_guard lock(*m_freeLock);
        if (!m_free->empty()) { index = m_free->back(); m_free->pop_back(); }
    }
    if (index < 0) { ++m_dropped; return; }
    if (time <= m_lastSubmit) time = m_lastSubmit + 1;

    // Fit the picture in the clip's frame, centred, black around it.
    double scale = std::min(double(m_width) / m_content.cx, double(m_height) / m_content.cy);
    LONG dw = static_cast<LONG>(m_content.cx * scale) & ~1L, dh = static_cast<LONG>(m_content.cy * scale) & ~1L;
    LONG x = ((static_cast<LONG>(m_width) - dw) / 2) & ~1L, y = ((static_cast<LONG>(m_height) - dh) / 2) & ~1L;
    RECT source{ 0, 0, m_content.cx, m_content.cy };
    RECT dest{ x, y, x + dw, y + dh };
    RECT full{ 0, 0, static_cast<LONG>(m_width), static_cast<LONG>(m_height) };
    m_videoContext->VideoProcessorSetStreamSourceRect(m_vp.get(), 0, TRUE, &source);
    m_videoContext->VideoProcessorSetStreamDestRect(m_vp.get(), 0, TRUE, &dest);
    m_videoContext->VideoProcessorSetOutputTargetRect(m_vp.get(), TRUE, &full);
    D3D11_VIDEO_PROCESSOR_STREAM stream{};
    stream.Enable = TRUE;
    stream.pInputSurface = input;
    HRESULT hr = m_videoContext->VideoProcessorBlt(m_vp.get(), m_targets[index].view.get(), 0, 1, &stream);
    if (FAILED(hr))
    {
        std::lock_guard lock(*m_freeLock);
        m_free->push_back(index);
        ++m_dropped;
        return;
    }
    m_lastIndex = index;
    m_lastDest = dest;
    if (m_options.realtime)
    {
        // A stream sends on its own clock: this picture waits for the next tick, and one that waited
        // and is already overtaken goes back unused.
        if (m_pending >= 0)
        {
            std::lock_guard lock(*m_freeLock);
            m_free->push_back(m_pending);
        }
        m_pending = index;
        return;
    }
    Submit(index, time);
}

void CaptureSession::Submit(int index, int64_t time)
{
    // A key frame every second: clips and trims start within a second of where asked. A stream
    // gets one every five (and whenever Discord asks).
    if (time - m_lastKey >= m_keyInterval)
    {
        m_encoder->ForceKeyFrame();
        m_lastKey = time;
    }
    auto free = m_free;
    auto freeLock = m_freeLock;
    m_encoder->Submit(m_targets[index].texture.get(), time, [free, freeLock, index] {
        std::lock_guard lock(*freeLock);
        free->push_back(index);
    });
    m_lastSubmit = time;
    ++m_frames;
}

void CaptureSession::Repeat()
{
    // A still picture sends no frames. For a clip the last one is repeated ten times a second, so key
    // frames keep coming and the sound stays in step. Go Live runs on its own clock instead: a frame
    // exactly every 1/30 or 1/60 s, each the newest picture (a new one when the screen changed, the
    // last one again when it did not), so viewers get a steady rate, never one that drifts with the
    // monitor's or the game's.
    const bool live = m_options.realtime;
    const int64_t step = live ? m_interval : 500'000;
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    int64_t next = Now100ns();
    while (!m_stop)
    {
        next += step;
        int64_t wait = next - Now100ns();
        // Fallen behind by more than a frame (the PC stalled): start the beat again from now.
        if (wait < -m_interval) { next = Now100ns(); wait = 0; }
        if (wait > 0)
        {
            LARGE_INTEGER due{};
            due.QuadPart = -wait;
            if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 1000);
            else Sleep(static_cast<DWORD>(std::max<int64_t>(1, wait / 10'000)));
        }
        if (m_stop) break;
        std::lock_guard lock(m_convert);
        if (m_closed || m_lastIndex < 0) continue;
        const int64_t time = std::max(live ? next : Now100ns(), m_lastSubmit + 1);
        try
        {
            if (live && m_pending >= 0)
            {
                int index = m_pending;
                m_pending = -1;
                Submit(index, time);
                continue;
            }
            if (!live && time - m_lastSubmit <= 1'000'000) continue;
            int index = -1;
            {
                std::lock_guard free(*m_freeLock);
                if (!m_free->empty()) { index = m_free->back(); m_free->pop_back(); }
            }
            if (index < 0) { ++m_dropped; continue; }
            // The newest picture again: no new frame means nothing on screen changed.
            if (index != m_lastIndex) m_gpu->context->CopyResource(m_targets[index].texture.get(), m_targets[m_lastIndex].texture.get());
            m_lastIndex = index;
            Submit(index, time);
        }
        catch (const std::exception& e) { Log(std::string("Repeat frame: ") + e.what()); }
    }
    if (timer) CloseHandle(timer);
}

bool CaptureSession::LastFrame(std::vector<uint8_t>& pixels, UINT& width, UINT& height, UINT maxWidth)
{
    // Only the copy holds the converter: the read back waits on the GPU, and frames keep coming.
    D3D11_TEXTURE2D_DESC desc{};
    com_ptr<ID3D11Texture2D> staging;
    RECT r{};
    {
        std::lock_guard lock(m_convert);
        if (m_hdr || m_lastIndex < 0) return false;
        m_targets[m_lastIndex].texture->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        if (FAILED(m_gpu->device->CreateTexture2D(&desc, nullptr, staging.put()))) return false;
        m_gpu->context->CopyResource(staging.get(), m_targets[m_lastIndex].texture.get());
        r = m_lastDest;
    }
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(m_gpu->context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    // The newest converted (NV12) frame's picture area as BGRA, sampled to at most maxWidth across:
    // it only becomes a thumbnail or a preview.
    UINT fullW = static_cast<UINT>(r.right - r.left), fullH = static_cast<UINT>(r.bottom - r.top);
    UINT step = std::max(1u, (fullW + maxWidth - 1) / std::max(1u, maxWidth));
    width = fullW / step;
    height = fullH / step;
    pixels.resize(static_cast<size_t>(width) * height * 4);
    auto* base = static_cast<const uint8_t*>(mapped.pData);
    const uint8_t* chroma = base + static_cast<size_t>(mapped.RowPitch) * desc.Height;
    auto clamp = [](double v) { return static_cast<uint8_t>(std::clamp(v, 0.0, 255.0)); };
    for (UINT y = 0; y < height; ++y)
    {
        UINT sy = r.top + y * step;
        const uint8_t* luma = base + static_cast<size_t>(mapped.RowPitch) * sy;
        const uint8_t* uv = chroma + static_cast<size_t>(mapped.RowPitch) * (sy / 2);
        uint8_t* out = pixels.data() + static_cast<size_t>(y) * width * 4;
        for (UINT x = 0; x < width; ++x)
        {
            UINT sx = r.left + x * step;
            // BT.709, studio range.
            double Y = 1.164 * (luma[sx] - 16);
            double U = uv[sx & ~1u] - 128.0, V = uv[(sx & ~1u) + 1] - 128.0;
            out[x * 4 + 0] = clamp(Y + 2.112 * U);
            out[x * 4 + 1] = clamp(Y - 0.213 * U - 0.533 * V);
            out[x * 4 + 2] = clamp(Y + 1.793 * V);
            out[x * 4 + 3] = 255;
        }
    }
    m_gpu->context->Unmap(staging.get(), 0);
    return width > 0 && height > 0;
}
