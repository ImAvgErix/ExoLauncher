// Exo Capture - GPU device, Windows.Graphics.Capture, color conversion and the hardware video encoder.
// GPL-3.0-or-later.
#pragma once
#include "common.h"
#include "mux.h"
#include "hook.h"

#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

/// One D3D11 device shared by capture, color conversion and the encoder (no copies through the CPU).
struct GpuDevice
{
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<IMFDXGIDeviceManager> manager;
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice winrt{ nullptr };
    LUID luid{};
    std::wstring adapter;

    static std::shared_ptr<GpuDevice> Create();
};

enum class Codec { H264, HEVC, AV1 };
const wchar_t* CodecName(Codec codec);

GUID Subtype(Codec codec);
/// The MFTs for a category and types; true and the chosen one's name when any matches the adapter (the one that
/// says it is on this adapter wins). Used by the encoder and by Shrink.
bool FindMft(const GUID& category, UINT32 flags, const GUID& inSub, const GUID& outSub, const LUID* luid,
    std::wstring* name, com_ptr<IMFActivate>* chosen);

/// What this PC's GPU can encode in hardware, and play back.
struct EncoderSupport
{
    bool h264 = false, hevc = false, av1 = false;
    bool hevcPlayback = false, av1Playback = false;
    std::wstring name;
    /// AV1 when the GPU encodes it, else HEVC, else H.264; only a codec Windows can play back.
    Codec Best() const;
};
EncoderSupport ProbeEncoders(const GpuDevice& gpu);
/// The parameter sets (VPS/SPS/PPS) in an Annex B key frame, each with a four-byte start code; empty when it has none.
std::vector<uint8_t> ParameterSets(const std::vector<uint8_t>& data, Codec codec);

/// The GPU's hardware encoder (NVENC, AMF or Quick Sync through its Media Foundation transform), async.
class VideoEncoder
{
public:
    /// realtime: a live stream (Go Live) at a constant rate the network carries, a key frame on request and
    /// every minute; else a file's quality-led rate, a key frame every two seconds.
    /// software: Windows' own H.264 encoder on the CPU (Go Live when the graphics card's won't start), fed from memory.
    VideoEncoder(GpuDevice& gpu, Codec codec, UINT width, UINT height, UINT fps, UINT bitrate, bool hdr, bool realtime,
        std::function<void(PacketPtr)> sink, bool software = false);
    ~VideoEncoder();
    /// Queues one NV12/P010 texture; released() runs once the encoder no longer reads it.
    void Submit(ID3D11Texture2D* texture, int64_t time, std::function<void()> released);
    void ForceKeyFrame() { m_forceKey = true; }
    /// A new bit rate from the next frame on (Go Live following what Discord's server says the stream can take).
    void SetBitrate(UINT bitrate) { m_newRate = bitrate; }
    /// The output type with its sequence header, once the first frame is out (waits up to 3 s).
    com_ptr<IMFMediaType> OutputType();

private:
    void EventLoop();
    void FeedLocked();
    bool TakeOutput();
    void SubmitSync(ID3D11Texture2D* texture, int64_t time);
    void TakeRequests();

    GpuDevice& m_gpu;
    Codec m_codec;
    UINT m_fps;
    UINT m_width, m_height;
    com_ptr<IMFTransform> m_mft;
    com_ptr<IMFMediaEventGenerator> m_events;
    com_ptr<ICodecAPI> m_api;
    DWORD m_in = 0, m_out = 0;
    std::wstring m_name;
    std::function<void(PacketPtr)> m_sink;

    std::mutex m_lock;
    std::condition_variable m_changed;
    std::deque<com_ptr<IMFSample>> m_queue;
    int m_credits = 0;
    std::atomic<bool> m_forceKey{ false };
    std::atomic<UINT> m_newRate{ 0 };
    bool m_drained = false;
    com_ptr<IMFMediaType> m_outputType;
    std::thread m_thread;
    bool m_sync = false; // the CPU encoder: no events, each frame in and its output out on the caller's thread
    com_ptr<ID3D11Texture2D> m_staging;
};

/// Captures one window or monitor with Windows.Graphics.Capture (no injection), converts each
/// frame to the encoder's format on the GPU, and feeds the encoder.
class CaptureSession
{
public:
    struct Options
    {
        HWND window = nullptr;
        HMONITOR monitor = nullptr;
        UINT fps = 60;
        Codec codec = Codec::HEVC;
        /// HEVC is the step down from AV1 (encoded and played back here); else H.264.
        bool hevcFallback = false;
        int quality = 50;
        bool cursor = true;
        /// Take the game's frames from inside it (when Windows capture gets none).
        bool gameHook = false;
        /// The encoded picture's size (even); 0 keeps the content's own, up to 4K.
        UINT width = 0, height = 0;
        /// Bits per second; 0 works it out from quality.
        UINT bitrate = 0;
        /// Go Live: a constant bit rate for the network and a key frame on request (and every minute).
        bool realtime = false;
    };

    CaptureSession(std::shared_ptr<GpuDevice> gpu, const Options& options, std::function<void(PacketPtr)> sink);
    ~CaptureSession();

    UINT Width() const { return m_width; }
    UINT Height() const { return m_height; }
    UINT Fps() const { return m_fps; }
    UINT Bitrate() const { return m_bitrate; }
    bool Hdr() const { return m_hdr; }
    Codec CodecUsed() const { return m_codec; }
    /// False once the window closed.
    bool Alive() const { return !m_closed; }
    /// True when frames come from inside the game rather than through Windows.
    bool FromGame() const { return m_hook != nullptr; }
    /// The captured content's size now (a game may change resolution).
    SIZE ContentSize() const;
    com_ptr<IMFMediaType> VideoType() { return m_encoder->OutputType(); }
    void ForceKeyFrame() { if (m_encoder) m_encoder->ForceKeyFrame(); }
    void SetBitrate(UINT bitrate) { if (m_encoder) m_encoder->SetBitrate(bitrate); }
    /// The newest frame as BGRA pixels at most maxWidth across, for a thumbnail. False when there is none (or HDR).
    bool LastFrame(std::vector<uint8_t>& pixels, UINT& width, UINT& height, UINT maxWidth = 480);
    /// Frames converted and dropped since the start (dropped: the encoder was still busy).
    uint64_t Frames() const { return m_frames; }
    uint64_t Dropped() const { return m_dropped; }
    /// Frames Windows handed over (none from a game in exclusive full screen).
    uint64_t Arrived() const { return m_arrived; }

private:
    void OnFrame(winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool const& pool);
    /// A free converted-frame texture, or a new one while there are fewer than MaxTargets; -1 when all are in use.
    int TakeTarget();
    int AddTarget();
    void EnsureProcessor(UINT width, UINT height, DXGI_FORMAT format);
    ID3D11VideoProcessorInputView* InputFor(ID3D11Texture2D* texture, UINT width, UINT height);
    void Convert(int64_t time, ID3D11VideoProcessorInputView* input);
    void Submit(int index, int64_t time);
    void Repeat();
    void Pump();
    void StartWindows(const Options& options);

    std::shared_ptr<GpuDevice> m_gpu;
    Options m_options;
    Codec m_codec;
    UINT m_width = 0, m_height = 0, m_fps = 60, m_bitrate = 0;
    bool m_hdr = false;
    bool m_software = false;
    int64_t m_interval = 166'667;
    int64_t m_keyInterval = 10'000'000;

    winrt::Windows::Graphics::Capture::GraphicsCaptureItem m_item{ nullptr };
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool m_pool{ nullptr };
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession m_session{ nullptr };
    winrt::Windows::Graphics::SizeInt32 m_poolSize{};
    std::atomic<bool> m_closed{ false };

    std::mutex m_convert;
    // The converter reads the capture's own surfaces; a copy only where the driver will not.
    UINT m_inW = 0, m_inH = 0;
    DXGI_FORMAT m_inFormat = DXGI_FORMAT_UNKNOWN;
    struct Input { com_ptr<ID3D11Texture2D> texture; com_ptr<ID3D11VideoProcessorInputView> view; };
    std::vector<Input> m_inputs;
    bool m_direct = true;
    com_ptr<ID3D11Texture2D> m_copy;
    com_ptr<ID3D11VideoProcessorInputView> m_copyView;
    int m_lastIndex = -1; // the target holding the newest converted frame
    int m_pending = -1;   // Go Live: the newest converted frame not sent yet (the stream's clock sends it)
    RECT m_lastDest{};
    SIZE m_content{};
    com_ptr<ID3D11VideoDevice> m_videoDevice;
    com_ptr<ID3D11VideoContext> m_videoContext;
    com_ptr<ID3D11VideoProcessorEnumerator> m_vpEnum;
    com_ptr<ID3D11VideoProcessor> m_vp;
    struct Target { com_ptr<ID3D11Texture2D> texture; com_ptr<ID3D11VideoProcessorOutputView> view; };
    static constexpr size_t MaxTargets = 6;
    std::vector<Target> m_targets; // grown only under m_convert
    std::shared_ptr<std::vector<int>> m_free = std::make_shared<std::vector<int>>();
    std::shared_ptr<std::mutex> m_freeLock = std::make_shared<std::mutex>();
    int64_t m_lastFrame = 0;
    int64_t m_lastSubmit = 0;
    int64_t m_lastKey = 0;
    std::atomic<uint64_t> m_frames{ 0 }, m_dropped{ 0 }, m_arrived{ 0 };

    std::unique_ptr<VideoEncoder> m_encoder;
    std::atomic<bool> m_stop{ false };
    std::thread m_repeat;
    std::unique_ptr<GameHook> m_hook;
    std::thread m_pump;
};

/// One picture of a window or screen as a JPEG at most maxWidth across, for Go Live's picker and
/// preview (no cursor, no border). Empty when Windows gives no picture within a second (a minimized window).
std::vector<uint8_t> GrabFrame(GpuDevice& gpu, HWND window, HMONITOR monitor, UINT maxWidth);

/// Bits per second for a quality from 0 (smaller files) to 100 (best quality).
UINT BitrateFor(Codec codec, UINT width, UINT height, UINT fps, int quality);
/// The refresh rate of a monitor, in Hz.
UINT RefreshRate(HMONITOR monitor);
/// True when Windows HDR is on for that monitor.
bool MonitorIsHdr(HMONITOR monitor);
/// An HDR monitor's picture as an sRGB snip that looks as it did on screen: SDR content exact, HDR
/// highlights rolled off instead of clipped. False when it is not in HDR or the picture is not
/// available; bounds is its place on the desktop.
bool CaptureHdrMonitor(HMONITOR monitor, std::vector<uint8_t>& bgra, RECT& bounds);
