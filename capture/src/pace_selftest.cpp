// Exo Capture - self-test of a clip's frame pacing: a window of its own draws a new picture at a set rate (a game faster
// than the clip, at its rate, and slower), Exo Capture records it through Windows as a 60 fps clip, and the clip's frames
// are checked: the rate asked for, evenly spaced, none of the game's lost. GPL-3.0-or-later.
//
//   ExoCapturePaceTest.exe [--seconds <n>]
//
// Built and run by `pwsh -File capture\build.ps1 -Test`. The window is small, on a screen other than the main one when
// there is one, at the bottom of the window order (behind everything, so it shows only on an empty desktop), never takes
// the focus, and closes after each run. Exit code 0 when every check passed.
#include "video.h"

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
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

/// Where the window goes: the work area of a screen other than the main one, else the main one's.
RECT TestArea()
{
    struct Found { RECT main{}, other{}; bool hasOther = false; } found;
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR monitor, HDC, LPRECT, LPARAM data) -> BOOL {
        auto& f = *reinterpret_cast<Found*>(data);
        MONITORINFO info{ sizeof(info) };
        if (!GetMonitorInfoW(monitor, &info)) return TRUE;
        if (info.dwFlags & MONITORINFOF_PRIMARY) f.main = info.rcWork;
        else if (!f.hasOther) { f.other = info.rcWork; f.hasOther = true; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&found));
    return found.hasOther ? found.other : found.main;
}

/// A window of its own drawing a new color fps times a second on its own clock, as a game does.
class Source
{
public:
    explicit Source(double fps) : m_fps(fps)
    {
        m_thread = std::thread([this] {
            try { Run(); }
            catch (const std::exception& e) { Log(std::string("Pace test window: ") + e.what()); }
            m_ready = true;
        });
        while (!m_ready) Sleep(5);
    }
    ~Source()
    {
        m_stop = true;
        if (m_thread.joinable()) m_thread.join();
    }
    HWND Window() const { return m_window; }
    uint64_t Presents() const { return m_presents; }

private:
    void Run()
    {
        WNDCLASSW cls{};
        cls.lpfnWndProc = DefWindowProcW;
        cls.hInstance = GetModuleHandleW(nullptr);
        cls.lpszClassName = L"ExoCapturePaceTest";
        RegisterClassW(&cls);
        const int size = 160;
        const RECT area = TestArea();
        HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls.lpszClassName, L"Exo Capture pace test", WS_POPUP,
            area.right - size, area.bottom - size, size, size, nullptr, nullptr, cls.hInstance, nullptr);
        if (!window) throw std::runtime_error("no window");
        SetWindowPos(window, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        com_ptr<ID3D11Device> device;
        com_ptr<ID3D11DeviceContext> context;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, device.put(), nullptr, context.put()), "test window device");
        com_ptr<IDXGIFactory2> factory;
        Check(CreateDXGIFactory2(0, IID_PPV_ARGS(factory.put())), "test window factory");
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = size;
        desc.Height = size;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        com_ptr<IDXGISwapChain1> chain;
        Check(factory->CreateSwapChainForHwnd(device.get(), window, &desc, nullptr, nullptr, chain.put()), "test window swap chain");
        m_window = window;
        m_ready = true;

        HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        const int64_t step = static_cast<int64_t>(10'000'000 / m_fps);
        int64_t next = Now100ns();
        while (!m_stop)
        {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&msg);
            com_ptr<ID3D11Texture2D> back;
            chain->GetBuffer(0, IID_PPV_ARGS(back.put()));
            com_ptr<ID3D11RenderTargetView> view;
            device->CreateRenderTargetView(back.get(), nullptr, view.put());
            const float n = static_cast<float>(m_presents % 256) / 255.f;
            const float color[4] = { n, 1.f - n, 0.5f, 1.f };
            context->ClearRenderTargetView(view.get(), color);
            chain->Present(0, 0);
            ++m_presents;
            next += step;
            const int64_t wait = next - Now100ns();
            if (wait <= 0) continue;
            LARGE_INTEGER due{};
            due.QuadPart = -wait;
            if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 1000);
            else Sleep(static_cast<DWORD>(wait / 10'000));
        }
        if (timer) CloseHandle(timer);
        DestroyWindow(window);
    }

    const double m_fps;
    std::thread m_thread;
    std::atomic<bool> m_stop{ false }, m_ready{ false };
    std::atomic<HWND> m_window{ nullptr };
    std::atomic<uint64_t> m_presents{ 0 };
};

/// A game drawing at fps recorded as a 60 fps clip for some seconds (after one to settle), and the clip's frames checked.
void PaceOf(const std::shared_ptr<GpuDevice>& gpu, double fps, const char* what, int seconds)
{
    printf("\n== a game at %.0f fps (%s), recorded as a 60 fps clip for %d s\n", fps, what, seconds);
    Source source(fps);
    if (!source.Window()) { Expect(false, "the test window opened"); return; }
    std::mutex lock;
    std::vector<int64_t> times;
    std::atomic<bool> counting{ false };
    CaptureSession::Options options;
    options.window = source.Window();
    options.fps = 60;
    options.cursor = false;
    options.codec = Codec::H264;
    auto session = std::make_unique<CaptureSession>(gpu, options, [&](PacketPtr packet) {
        if (packet->track != 0 || !counting) return;
        std::lock_guard guard(lock);
        times.push_back(packet->time);
    });
    Sleep(1000);
    const uint64_t presentsBefore = source.Presents(), arrivedBefore = session->Arrived();
    counting = true;
    Sleep(seconds * 1000);
    counting = false;
    const double drawn = static_cast<double>(source.Presents() - presentsBefore) / seconds;
    const double arrived = static_cast<double>(session->Arrived() - arrivedBefore) / seconds;
    session.reset();

    std::vector<int64_t> frames;
    {
        std::lock_guard guard(lock);
        frames = times;
    }
    std::sort(frames.begin(), frames.end());
    if (frames.size() < 2) { Expect(false, "the clip got frames"); return; }
    const double span = (frames.back() - frames.front()) / 1e7;
    const double rate = (frames.size() - 1) / span;
    int uneven = 0, holes = 0;
    double longest = 0;
    for (size_t i = 1; i < frames.size(); ++i)
    {
        const double gap = (frames[i] - frames[i - 1]) / 1e4;
        longest = std::max(longest, gap);
        if (std::abs(gap - 1000.0 / 60) > 2) ++uneven;
        if (gap > 25) ++holes;
    }
    printf("  info  the window drew %.1f pictures a second; Windows handed over %.1f; the clip has %zu frames in %.2f s\n",
        drawn, arrived, frames.size(), span);
    if (fps >= 60)
    {
        // A game at least as fast as the clip fills every frame of it: none lost, none bunched.
        Expect(rate >= 59.4 && rate <= 60.3, Fmt("the clip runs at %.2f fps (60 asked)", rate));
        Expect(holes <= static_cast<int>(frames.size() / 200), Fmt("%d frames missing (a gap over 25 ms) of %zu; the longest gap %.1f ms", holes, frames.size(), longest));
        Expect(uneven <= static_cast<int>(frames.size() / 100), Fmt("%d of %zu gaps off 16.7 ms by over 2 ms", uneven, frames.size() - 1));
    }
    else
    {
        // A slower game: each of its pictures once, at the rate it drew them. One that lands just past a tick's wait
        // takes the next tick, so two ticks in a row can pass without a new one.
        Expect(std::abs(rate - drawn) <= drawn * 0.01, Fmt("no picture left out: the clip runs at %.2f fps, as the game drew (%.1f)", rate, drawn));
        Expect(longest <= 3 * 1000.0 / 60 + 2, Fmt("the longest gap %.1f ms, three frames at most", longest));
    }
}
}

int wmain(int argc, wchar_t** argv)
{
    setvbuf(stdout, nullptr, _IONBF, 0);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    Check(MFStartup(MF_VERSION), "Media Foundation");
    int seconds = 8;
    for (int i = 1; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = std::clamp(_wtoi(argv[++i]), 2, 120);
    try
    {
        auto gpu = GpuDevice::Create();
        PaceOf(gpu, 150, "faster than the clip", seconds);
        PaceOf(gpu, 60, "at the clip's own rate", seconds);
        PaceOf(gpu, 45, "slower than the clip", seconds);
    }
    catch (const std::exception& e)
    {
        Expect(false, std::string("the test ran: ") + e.what());
    }
    printf("\n%d checks, %d failed\n", g_checks, g_failed);
    MFShutdown();
    return g_failed ? 1 : 0;
}
