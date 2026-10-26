// Exo Capture - instant replay, recording, snips and Go Live for Exo Launcher. GPL-3.0-or-later.
//
// Exo starts this program and talks to it over its standard input and output only
// (one JSON object per line), so no other program can reach it: there is no port
// and no named pipe. Requests: {"id", "method", "params"}; replies: {"id", "result"}
// or {"id", "error"}; events: {"event", "data"}. Go Live's encoded frames go back on an
// anonymous pipe whose write end Exo duplicates into this process (see Streamer).
#include "common.h"
#include "audio.h"
#include "mux.h"
#include "shrink.h"
#include "ui.h"
#include "video.h"

#include <psapi.h>
#include <shellapi.h>

#include <future>
#include <iostream>

namespace
{
enum HotkeyId { SaveReplayKey = 1, RecordKey = 2, SnipKey = 3, PrintScreenKey = 4 };

struct Settings
{
    int quality = 50;
    int maxFps = 60; // 60, or up to 120 where the screen runs that fast
    int replaySeconds = 60;
    std::wstring replayMode = L"games"; // games | always | off
    bool cursor = true;
    std::wstring folder;
    std::wstring thumbs;
    std::wstring keys[3] = { L"Ctrl+Alt+F10", L"Ctrl+Alt+F9", L"Ctrl+Alt+F8" };
    bool printScreen = false;
    std::wstring font;
};

/// Runs jobs one at a time on its own thread, so session changes never overlap.
class Jobs
{
public:
    Jobs() : m_thread([this] { Run(); }) {}
    ~Jobs()
    {
        { std::lock_guard lock(m_lock); m_stop = true; }
        m_wake.notify_one();
        if (m_thread.joinable()) m_thread.join();
    }
    void Post(std::function<void()> job)
    {
        { std::lock_guard lock(m_lock); m_jobs.push_back(std::move(job)); }
        m_wake.notify_one();
    }
    void Invoke(const std::function<void()>& job)
    {
        std::promise<void> done;
        auto ready = done.get_future();
        Post([&] { job(); done.set_value(); });
        ready.wait();
    }

private:
    void Run()
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        for (;;)
        {
            std::function<void()> job;
            {
                std::unique_lock lock(m_lock);
                m_wake.wait(lock, [this] { return m_stop || !m_jobs.empty(); });
                if (m_jobs.empty()) return;
                job = std::move(m_jobs.front());
                m_jobs.pop_front();
            }
            try { job(); }
            catch (const std::exception& e) { Log(std::string("Job: ") + e.what()); }
            catch (winrt::hresult_error const& e) { Log("Job: " + Narrow(std::wstring(e.message()))); }
        }
    }
    std::mutex m_lock;
    std::condition_variable m_wake;
    std::deque<std::function<void()>> m_jobs;
    bool m_stop = false;
    std::thread m_thread;
};

std::wstring Lower(std::wstring s)
{
    CharLowerBuffW(s.data(), static_cast<DWORD>(s.size()));
    return s;
}

std::wstring FullPath(const std::wstring& path)
{
    wchar_t buffer[MAX_PATH * 4];
    DWORD n = GetFullPathNameW(path.c_str(), ARRAYSIZE(buffer), buffer, nullptr);
    return n && n < ARRAYSIZE(buffer) ? std::wstring(buffer, n) : path;
}

bool IsUnder(const std::wstring& path, const std::wstring& folder)
{
    if (folder.empty()) return false;
    auto p = Lower(FullPath(path)), f = Lower(FullPath(folder));
    if (!f.empty() && f.back() != L'\\') f += L'\\';
    return p.rfind(f, 0) == 0 && p.find(L"..") == std::wstring::npos;
}

std::wstring ProcessImage(DWORD pid)
{
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return {};
    wchar_t path[MAX_PATH * 2];
    DWORD size = ARRAYSIZE(path);
    std::wstring out;
    if (QueryFullProcessImageNameW(process, 0, path, &size)) out.assign(path, size);
    CloseHandle(process);
    return out;
}

bool Pickable(HWND hwnd)
{
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindow(hwnd, GW_OWNER)) return false;
    if (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return false;
    RECT r;
    GetWindowRect(hwnd, &r);
    return r.right - r.left >= 200 && r.bottom - r.top >= 150;
}

/// The game's main window: a visible window of a process under its install folder,
/// the foreground one first, else the largest.
HWND FindGameWindow(const std::wstring& folder)
{
    if (folder.empty()) return nullptr;
    struct Ctx { std::wstring folder; HWND best = nullptr; long long area = 0; HWND foreground; } ctx{ Lower(FullPath(folder)), nullptr, 0, GetForegroundWindow() };
    if (!ctx.folder.empty() && ctx.folder.back() != L'\\') ctx.folder += L'\\';
    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto* c = reinterpret_cast<Ctx*>(lp);
        if (!Pickable(hwnd)) return TRUE;
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        auto image = Lower(ProcessImage(pid));
        if (image.rfind(c->folder, 0) != 0) return TRUE;
        RECT r;
        GetWindowRect(hwnd, &r);
        long long area = static_cast<long long>(r.right - r.left) * (r.bottom - r.top) + (hwnd == c->foreground ? (1LL << 40) : 0);
        if (area > c->area) { c->area = area; c->best = hwnd; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return ctx.best;
}

/// FNV-1a of the path's UTF-8: the thumbnail's file name, which Exo works out the same way.
std::wstring ThumbName(const std::wstring& path)
{
    uint64_t hash = 14695981039346656037ull;
    for (unsigned char c : Narrow(path)) { hash ^= c; hash *= 1099511628211ull; }
    wchar_t text[17];
    swprintf_s(text, L"%016llx", static_cast<unsigned long long>(hash));
    return text;
}

/// Go Live (Exo's Discord client): one window or screen encoded for Discord's servers (H.264, a
/// constant bit rate, a key frame whenever Discord asks and every minute) and written to the
/// pipe Exo made and handed over (its write end duplicated into this process; no other program has
/// it). Per frame: a 16-byte header (u32 length, u32 flags with 1 for a key frame, i64 capture time
/// in 100 ns) and the frame (Annex B, its SPS and PPS in front of every key frame). Exo encrypts
/// and sends it; the picture never passes through Exo's page.
class Streamer
{
public:
    Streamer(std::shared_ptr<GpuDevice> gpu, const CaptureSession::Options& options, HANDLE pipe, std::function<void(const char*)> ended)
        : m_pipe(pipe), m_ended(std::move(ended)), m_codec(options.codec)
    {
        m_video = std::make_unique<CaptureSession>(std::move(gpu), options, [this](PacketPtr packet) { OnPacket(packet); });
        m_codec = m_video->CodecUsed(); // HEVC asked for, H.264 when the card's encoder refused it
        m_writer = std::thread([this] { Write(); });
        m_watch = std::thread([this] { Watch(); });
    }

    ~Streamer()
    {
        {
            std::lock_guard lock(m_lock);
            m_stop = true;
        }
        m_wake.notify_all();
        // A write Exo stopped reading blocks: cancel it rather than wait.
        if (m_writer.joinable())
        {
            CancelSynchronousIo(m_writer.native_handle());
            m_writer.join();
        }
        if (m_watch.joinable()) m_watch.join();
        m_video.reset();
        CloseHandle(m_pipe);
    }

    CaptureSession& Session() { return *m_video; }

private:
    void OnPacket(const PacketPtr& packet)
    {
        if (packet->track != 0 || m_stop) return;
        PacketPtr out = packet;
        std::lock_guard lock(m_lock);
        if (packet->key)
        {
            // A viewer joins at a key frame: it must carry the parameter sets, whatever the encoder does.
            auto sets = ParameterSets(packet->data, m_codec.load());
            if (!sets.empty()) m_sets = std::move(sets);
            else if (!m_sets.empty())
            {
                auto copy = std::make_shared<Packet>(*packet);
                copy->data.insert(copy->data.begin(), m_sets.begin(), m_sets.end());
                out = copy;
            }
        }
        if (m_skipping && !out->key) return;
        m_skipping = false;
        // Exo fell two seconds behind: start over at the next key frame rather than send old pictures.
        if (m_queue.size() >= 120)
        {
            m_queue.clear();
            if (!out->key) { m_skipping = true; m_wantKey = true; return; }
        }
        m_queue.push_back(out);
        m_wake.notify_one();
    }

    void Write()
    {
        for (;;)
        {
            PacketPtr packet;
            {
                std::unique_lock lock(m_lock);
                m_wake.wait(lock, [this] { return m_stop || !m_queue.empty(); });
                if (m_stop) return;
                packet = m_queue.front();
                m_queue.pop_front();
            }
            uint8_t header[16]{};
            uint32_t length = static_cast<uint32_t>(packet->data.size()), flags = packet->key ? 1u : 0u;
            int64_t time = packet->time;
            memcpy(header, &length, 4);
            memcpy(header + 4, &flags, 4);
            memcpy(header + 8, &time, 8);
            if (!WriteAll(header, sizeof(header)) || !WriteAll(packet->data.data(), length))
            {
                if (!m_stop) End("Exo stopped reading the stream");
                return;
            }
        }
    }

    bool WriteAll(const uint8_t* data, size_t length)
    {
        while (length > 0)
        {
            DWORD wrote = 0;
            if (!WriteFile(m_pipe, data, static_cast<DWORD>(std::min<size_t>(length, 1 << 20)), &wrote, nullptr) || wrote == 0) return false;
            data += wrote;
            length -= wrote;
        }
        return true;
    }

    void Watch()
    {
        while (!m_stop)
        {
            Sleep(100);
            if (m_wantKey.exchange(false)) m_video->ForceKeyFrame();
            if (!m_video->Alive()) { End("the window closed"); return; }
        }
    }

    void End(const char* why)
    {
        if (m_endedOnce.exchange(true)) return;
        Log(std::string("Go Live ended: ") + why + ".");
        m_ended(why);
    }

    HANDLE m_pipe;
    std::function<void(const char*)> m_ended;
    std::atomic<Codec> m_codec;
    std::mutex m_lock;
    std::condition_variable m_wake;
    std::deque<PacketPtr> m_queue;
    std::vector<uint8_t> m_sets;
    bool m_skipping = false;
    std::atomic<bool> m_stop{ false }, m_wantKey{ false }, m_endedOnce{ false };
    std::unique_ptr<CaptureSession> m_video;
    std::thread m_writer, m_watch;
};

HWND WindowParam(const json::JsonObject& p)
{
    auto hwnd = reinterpret_cast<HWND>(static_cast<intptr_t>(GetNumber(p, L"window", 0)));
    return hwnd && IsWindow(hwnd) ? hwnd : nullptr;
}

HMONITOR MonitorParam(const json::JsonObject& p)
{
    auto monitor = reinterpret_cast<HMONITOR>(static_cast<intptr_t>(GetNumber(p, L"monitor", 0)));
    MONITORINFO info{ sizeof(info) };
    return monitor && GetMonitorInfoW(monitor, &info) ? monitor : nullptr;
}

class Controller
{
public:
    Controller()
        : m_ui([this](int id) { OnHotkey(id); })
    {
        m_gpu = GpuDevice::Create();
        m_support = ProbeEncoders(*m_gpu);
        Log("Exo Capture on " + Narrow(m_gpu->adapter) + ", encoder " + Narrow(m_support.name) + ", codec " + Narrow(CodecName(m_support.Best())));
        m_ticker = std::thread([this] {
            while (!m_quit)
            {
                for (int i = 0; i < 20 && !m_quit; ++i) Sleep(100);
                if (!m_quit) m_jobs.Post([this] { Tick(); });
            }
        });
    }

    ~Controller() { Shutdown(); }

    void Shutdown()
    {
        if (m_quit.exchange(true)) return;
        // A shrink in progress stops and deletes its partial file before the process ends.
        m_shrinkCancel = true;
        for (int i = 0; i < 30; ++i)
        {
            { std::lock_guard lock(m_shrinkLock); if (!m_shrinking) break; }
            Sleep(100);
        }
        if (m_ticker.joinable()) m_ticker.join();
        StopStream(0, {}, false);
        m_jobs.Invoke([this] {
            StopRecording(false);
            StopSession();
        });
    }

    // ------------------------------------------------------------ requests

    json::IJsonValue Handle(const std::wstring& method, const json::JsonObject& p)
    {
        if (method == L"configure") { m_jobs.Invoke([&] { Configure(p); }); return State(); }
        if (method == L"game")
        {
            m_jobs.Invoke([&] {
                std::wstring folder = GetString(p, L"folder");
                if (folder != m_gameFolder || GetString(p, L"id") != m_gameId)
                {
                    m_gameId = GetString(p, L"id");
                    m_gameTitle = GetString(p, L"title");
                    m_gameFolder = folder;
                    m_gameWindow = nullptr;
                    Tick();
                }
            });
            return State();
        }
        if (method == L"replay.save") { m_jobs.Post([this] { SaveReplay(); }); return State(); }
        if (method == L"record.toggle") { m_jobs.Invoke([this] { ToggleRecording(); }); return State(); }
        if (method == L"snip") { StartSnip(); return json::JsonValue::CreateBooleanValue(true); }
        if (method == L"trim") return Trim(p);
        if (method == L"export") return Export(p);
        if (method == L"shrink") return ShrinkFile(p);
        if (method == L"shrink.cancel") return CancelShrink(p);
        if (method == L"copy") return CopyFile(p);
        if (method == L"probe") return Probe(Checked(p, L"path"));
        if (method == L"stream.thumbs") return Thumbs(p);
        if (method == L"stream.start") return StartStream(p);
        if (method == L"voice.feed")
        {
            StartVoiceFeed(reinterpret_cast<HANDLE>(static_cast<intptr_t>(GetNumber(p, L"pipe", 0))));
            return json::JsonValue::CreateBooleanValue(true);
        }
        if (method == L"stream.keyframe")
        {
            std::lock_guard lock(m_streamLock);
            if (m_stream) m_stream->Session().ForceKeyFrame();
            return json::JsonValue::CreateBooleanValue(m_stream != nullptr);
        }
        if (method == L"stream.bitrate")
        {
            auto rate = GetNumber(p, L"bitrate", 0);
            std::lock_guard lock(m_streamLock);
            if (m_stream && rate >= 100'000 && rate <= 200e6) m_stream->Session().SetBitrate(static_cast<UINT>(rate));
            return json::JsonValue::CreateBooleanValue(m_stream != nullptr);
        }
        if (method == L"stream.preview") return StreamPreview(p);
        if (method == L"stream.stop")
        {
            StopStream(static_cast<uint64_t>(GetNumber(p, L"id", 0)), {}, false);
            return json::JsonValue::CreateBooleanValue(true);
        }
        if (method == L"copyImage")
        {
            auto input = Checked(p, L"path");
            std::vector<uint8_t> pixels;
            UINT w = 0, h = 0;
            bool ok = ReadImageFile(input, pixels, w, h);
            if (ok) m_ui.Invoke([&] { ok = CopyImageToClipboard(m_ui.Window(), pixels.data(), w, h, w * 4); });
            return json::JsonValue::CreateBooleanValue(ok);
        }
        throw std::runtime_error("Unknown request.");
    }

    // ------------------------------------------------------------ state

    json::JsonObject State()
    {
        auto o = Obj();
        std::lock_guard lock(m_stateLock);
        o.Insert(L"buffering", json::JsonValue::CreateBooleanValue(m_buffering));
        o.Insert(L"recording", json::JsonValue::CreateBooleanValue(m_recording));
        o.Insert(L"recordingMs", json::JsonValue::CreateNumberValue(m_recording ? (Now100ns() - m_recordStart) / 10'000.0 : 0));
        o.Insert(L"bufferedMs", json::JsonValue::CreateNumberValue(m_buffer.Span() / 10'000.0));
        o.Insert(L"bufferBytes", json::JsonValue::CreateNumberValue(static_cast<double>(m_buffer.Bytes())));
        o.Insert(L"codec", json::JsonValue::CreateStringValue(m_video ? CodecName(m_video->CodecUsed()) : CodecName(m_support.Best())));
        o.Insert(L"encoder", json::JsonValue::CreateStringValue(m_support.name));
        o.Insert(L"av1", json::JsonValue::CreateBooleanValue(m_support.av1));
        o.Insert(L"hevc", json::JsonValue::CreateBooleanValue(m_support.hevc));
        if (m_video)
        {
            o.Insert(L"width", json::JsonValue::CreateNumberValue(m_video->Width()));
            o.Insert(L"height", json::JsonValue::CreateNumberValue(m_video->Height()));
            o.Insert(L"fps", json::JsonValue::CreateNumberValue(m_video->Fps()));
            o.Insert(L"bitrate", json::JsonValue::CreateNumberValue(m_video->Bitrate()));
            o.Insert(L"hdr", json::JsonValue::CreateBooleanValue(m_video->Hdr()));
            o.Insert(L"frames", json::JsonValue::CreateNumberValue(static_cast<double>(m_video->Frames())));
            o.Insert(L"dropped", json::JsonValue::CreateNumberValue(static_cast<double>(m_video->Dropped())));
            o.Insert(L"target", json::JsonValue::CreateStringValue(m_targetWindow ? L"window" : L"screen"));
        }
        auto keys = Obj();
        const wchar_t* names[3] = { L"saveReplay", L"record", L"snip" };
        for (int i = 0; i < 3; ++i)
        {
            auto k = Obj();
            k.Insert(L"keys", json::JsonValue::CreateStringValue(m_settings.keys[i]));
            k.Insert(L"ok", json::JsonValue::CreateBooleanValue(m_keyOk[i]));
            keys.Insert(names[i], k);
        }
        o.Insert(L"hotkeys", keys);
        o.Insert(L"printScreenOk", json::JsonValue::CreateBooleanValue(m_printOk));
        if (!m_error.empty()) o.Insert(L"error", json::JsonValue::CreateStringValue(m_error));
        return o;
    }

    void PostState() { Emit(L"state", State()); }

private:
    // ------------------------------------------------------------ Go Live

    /// A picture of each window or screen asked for, at once: {id: JPEG base64}; none for one
    /// Windows gives no picture (a minimized window).
    json::IJsonValue Thumbs(const json::JsonObject& p)
    {
        struct Item { std::wstring id; HWND window; HMONITOR monitor; std::vector<uint8_t> jpeg; };
        std::vector<Item> items;
        auto list = p.HasKey(L"items") ? p.GetNamedArray(L"items") : json::JsonArray();
        for (uint32_t i = 0; i < list.Size() && items.size() < 64; ++i)
        {
            if (list.GetAt(i).ValueType() != json::JsonValueType::Object) continue;
            auto o = list.GetObjectAt(i);
            Item item{ GetString(o, L"id"), WindowParam(o), MonitorParam(o), {} };
            if (!item.id.empty() && (item.window || item.monitor)) items.push_back(std::move(item));
        }
        UINT width = static_cast<UINT>(std::clamp(GetNumber(p, L"width", 360), 64.0, 1920.0));
        std::vector<std::thread> threads;
        for (auto& item : items)
            threads.emplace_back([this, &item, width] {
                winrt::init_apartment(winrt::apartment_type::multi_threaded);
                try { item.jpeg = GrabFrame(*m_gpu, item.window, item.window ? nullptr : item.monitor, width); }
                catch (const std::exception& e) { Log(std::string("Go Live picture: ") + e.what()); }
                catch (winrt::hresult_error const& e) { Log("Go Live picture: " + Narrow(std::wstring(e.message()))); }
            });
        for (auto& thread : threads) thread.join();
        auto out = Obj();
        for (auto& item : items)
            if (!item.jpeg.empty()) out.Insert(item.id, json::JsonValue::CreateStringValue(Base64(item.jpeg)));
        return out;
    }

    json::IJsonValue StartStream(const json::JsonObject& p)
    {
        auto pipe = reinterpret_cast<HANDLE>(static_cast<intptr_t>(GetNumber(p, L"pipe", 0)));
        if (!pipe || GetFileType(pipe) != FILE_TYPE_PIPE) throw std::runtime_error("Exo gave no pipe for the stream.");
        CaptureSession::Options options;
        options.window = WindowParam(p);
        options.monitor = options.window ? nullptr : MonitorParam(p);
        if (!options.window && !options.monitor)
        {
            CloseHandle(pipe);
            throw std::runtime_error("That window or screen is gone.");
        }
        // Up to 120 fps, never faster than the screen it comes from (a faster rate would only repeat frames).
        options.fps = static_cast<UINT>(std::clamp(GetNumber(p, L"fps", 30), 10.0, 120.0));
        options.fps = std::min<UINT>(options.fps, std::max<UINT>(30, RefreshRate(options.window ? MonitorFromWindow(options.window, MONITOR_DEFAULTTONEAREST) : options.monitor)));
        // H.264, or HEVC when Discord's server picked H.265 for the stream (every viewer plays it) and the card encodes it.
        options.codec = GetString(p, L"codec") == L"hevc" && m_support.hevc ? Codec::HEVC : Codec::H264;
        options.cursor = GetBool(p, L"cursor", true);
        options.width = static_cast<UINT>(std::clamp(GetNumber(p, L"width", 0), 0.0, 4096.0));
        options.height = static_cast<UINT>(std::clamp(GetNumber(p, L"height", 0), 0.0, 4096.0));
        options.bitrate = static_cast<UINT>(std::clamp(GetNumber(p, L"bitrate", 6e6), 5e5, 6e7));
        options.realtime = true;
        StopStream(0, {}, false);
        auto id = ++m_streamIds;
        std::unique_ptr<Streamer> stream;
        try
        {
            stream = std::make_unique<Streamer>(m_gpu, options, pipe, [this, id](const char* why) {
                std::string reason = why;
                std::thread([this, id, reason] { StopStream(id, reason, true); }).detach();
            });
        }
        catch (...)
        {
            CloseHandle(pipe);
            throw;
        }
        // The encoder's first frame says the picture comes through (it waits up to 3 s).
        if (!stream->Session().VideoType()) throw std::runtime_error("That window gives no picture; is it minimized?");
        auto& video = stream->Session();
        auto o = Obj();
        o.Insert(L"id", json::JsonValue::CreateNumberValue(static_cast<double>(id)));
        o.Insert(L"width", json::JsonValue::CreateNumberValue(video.Width()));
        o.Insert(L"height", json::JsonValue::CreateNumberValue(video.Height()));
        o.Insert(L"fps", json::JsonValue::CreateNumberValue(video.Fps()));
        o.Insert(L"bitrate", json::JsonValue::CreateNumberValue(video.Bitrate()));
        o.Insert(L"codec", json::JsonValue::CreateStringValue(video.CodecUsed() == Codec::HEVC ? L"hevc" : L"h264"));
        o.Insert(L"hevc", json::JsonValue::CreateBooleanValue(m_support.hevc));
        Log("Go Live: " + std::to_string(video.Width()) + "x" + std::to_string(video.Height()) + " @" + std::to_string(video.Fps()) +
            " " + Narrow(CodecName(video.CodecUsed())) + " " + std::to_string(video.Bitrate() / 1000) + " kbit/s" + (options.window ? " (window)" : " (screen)"));
        std::lock_guard lock(m_streamLock);
        m_stream = std::move(stream);
        m_streamId = id;
        return o;
    }

    /// What the stream sends now, as a JPEG at most width across; null before the first picture.
    json::IJsonValue StreamPreview(const json::JsonObject& p)
    {
        UINT width = static_cast<UINT>(std::clamp(GetNumber(p, L"width", 480), 64.0, 1920.0));
        std::vector<uint8_t> pixels;
        UINT w = 0, h = 0;
        {
            std::lock_guard lock(m_streamLock);
            if (!m_stream || !m_stream->Session().LastFrame(pixels, w, h, width)) return json::JsonValue::CreateNullValue();
        }
        return json::JsonValue::CreateStringValue(Base64(EncodeJpeg(pixels.data(), w, h, w * 4, 0)));
    }

    /// Stops the stream (id 0: whichever runs; else only that one, so a late stop never ends a newer
    /// stream). tell: it ended on its own, and Exo hears why.
    void StopStream(uint64_t id, const std::string& why, bool tell)
    {
        std::unique_ptr<Streamer> stream;
        {
            std::lock_guard lock(m_streamLock);
            if (!m_stream || (id && id != m_streamId)) return;
            stream = std::move(m_stream);
        }
        stream.reset();
        if (!tell) return;
        auto o = Obj();
        o.Insert(L"reason", json::JsonValue::CreateStringValue(Widen(why)));
        Emit(L"stream.ended", o);
    }

    // ------------------------------------------------------------ settings

    void Configure(const json::JsonObject& p)
    {
        Settings next = m_settings;
        next.quality = std::clamp(static_cast<int>(GetNumber(p, L"quality", next.quality)), 0, 100);
        next.maxFps = GetNumber(p, L"maxFps", next.maxFps) >= 120 ? 120 : 60;
        next.replaySeconds = std::clamp(static_cast<int>(GetNumber(p, L"replaySeconds", next.replaySeconds)), 15, 600);
        next.replayMode = GetString(p, L"replayMode", next.replayMode);
        next.cursor = GetBool(p, L"cursor", next.cursor);
        next.folder = GetString(p, L"folder", next.folder);
        next.thumbs = GetString(p, L"thumbs", next.thumbs);
        next.font = GetString(p, L"font", next.font);
        next.printScreen = GetBool(p, L"printScreen", next.printScreen);
        if (p.HasKey(L"hotkeys"))
        {
            auto k = p.GetNamedObject(L"hotkeys");
            next.keys[0] = GetString(k, L"saveReplay", next.keys[0]);
            next.keys[1] = GetString(k, L"record", next.keys[1]);
            next.keys[2] = GetString(k, L"snip", next.keys[2]);
        }
        bool restart = next.quality != m_settings.quality || next.cursor != m_settings.cursor || next.maxFps != m_settings.maxFps;
        {
            std::lock_guard lock(m_stateLock);
            m_settings = next;
        }
        m_ui.SetFont(next.font);
        for (int i = 0; i < 3; ++i)
        {
            bool ok = m_ui.SetHotkey(SaveReplayKey + i, ParseHotkey(next.keys[i]));
            std::lock_guard lock(m_stateLock);
            m_keyOk[i] = ok;
        }
        bool print = m_ui.SetHotkey(PrintScreenKey, next.printScreen ? Hotkey{ 0, VK_SNAPSHOT } : Hotkey{});
        {
            std::lock_guard lock(m_stateLock);
            m_printOk = print;
        }
        if (!m_recovered && !next.folder.empty())
        {
            m_recovered = true;
            std::thread([folder = next.folder, thumbs = next.thumbs] { RecoverRecordings(folder, thumbs); }).detach();
        }
        m_buffer.SetLength(static_cast<int64_t>(next.replaySeconds) * 10'000'000);
        // The replay's files: in Exo's capture data (the thumbnails' parent folder), else on the captures folder's drive.
        auto slash = next.thumbs.find_last_of(L"\\/");
        m_buffer.SetFolders({ slash == std::wstring::npos ? L"" : next.thumbs.substr(0, slash), next.folder });
        if (restart && m_video && !m_recorder) StopSession();
        m_configured = true;
        Tick();
    }

    /// Recordings cut short (Exo Capture or the PC stopped) are rebuilt from their journals.
    static void RecoverRecordings(const std::wstring& folder, const std::wstring& thumbs)
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        std::vector<std::wstring> journals;
        WIN32_FIND_DATAW game{};
        HANDLE games = FindFirstFileW((folder + L"\\*").c_str(), &game);
        if (games == INVALID_HANDLE_VALUE) return;
        do
        {
            if (!(game.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || game.cFileName[0] == L'.') continue;
            std::wstring sub = folder + L"\\" + game.cFileName;
            WIN32_FIND_DATAW file{};
            HANDLE files = FindFirstFileW((sub + L"\\*.exo-rec").c_str(), &file);
            if (files == INVALID_HANDLE_VALUE) continue;
            do journals.push_back(sub + L"\\" + file.cFileName);
            while (FindNextFileW(files, &file));
            FindClose(files);
        } while (FindNextFileW(games, &game));
        FindClose(games);
        for (auto& journal : journals)
        {
            try
            {
                int64_t duration = RecoverJournal(journal);
                auto mp4 = journal.substr(0, journal.size() - 8);
                MakeThumb(thumbs, mp4, duration / 2);
                Saved(L"recording", mp4, duration, L"");
                Log("Recovered a recording that was cut short (" + std::to_string(duration / 10'000'000) + " s).");
            }
            catch (const std::exception& e) { Log(std::string("Recording recovery: ") + e.what()); }
        }
    }

    // ------------------------------------------------------------ the session

    /// Starts, keeps or stops capture for what is wanted now. Runs every two seconds.
    void Tick()
    {
        if (!m_configured || m_quit) return;
        // A minimized game (alt-tab) keeps its window, its session and the replay in it; only a
        // window that closed or hid is looked for again.
        if (!m_gameFolder.empty() && (!m_gameWindow || !IsWindow(m_gameWindow) || !IsWindowVisible(m_gameWindow)))
            m_gameWindow = FindGameWindow(m_gameFolder);

        bool always = m_settings.replayMode == L"always";
        bool games = m_settings.replayMode == L"games";
        bool wantBuffer = always || (games && m_gameWindow);
        HWND window = m_gameWindow;

        if (m_video && !m_video->Alive())
        {
            Log("Capture target closed.");
            if (m_recorder) StopRecording(true);
            StopSession();
        }
        if (m_recorder) return; // a recording keeps its session as it is

        if (!wantBuffer)
        {
            if (m_video) StopSession();
            SetBuffering(false);
            return;
        }
        // A game window that appeared, closed, or changed size (a resolution change) restarts the session.
        bool targetChanged = m_video && m_targetWindow != window;
        if (m_video && !targetChanged)
        {
            auto content = m_video->ContentSize();
            bool resized = content.cx > 0 && (std::abs(content.cx - static_cast<LONG>(m_startedContent.cx)) > 32 ||
                std::abs(content.cy - static_cast<LONG>(m_startedContent.cy)) > 32);
            m_resizedTicks = resized ? m_resizedTicks + 1 : 0;
            if (m_resizedTicks >= 2) targetChanged = true;
        }
        // Windows capture is the lighter path (a game keeps its own flip to the screen), but a game in
        // exclusive full screen gives it no picture: then take the frames from inside the game.
        if (m_video && window && window != m_hookWindow && !m_video->FromGame() && m_video->Arrived() == 0 &&
            !IsIconic(window) && Now100ns() - m_sessionStart > 50'000'000)
        {
            Log("Windows capture gets no picture from the game; taking it from inside the game.");
            m_hookWindow = window;
            targetChanged = true;
        }
        if (targetChanged) StopSession();
        // A minimized window has no picture to start from: wait until it is back.
        if (!m_video && !(window && IsIconic(window))) StartSession(window);
        SetBuffering(m_video != nullptr);
    }

    void StartSession(HWND window)
    {
        try
        {
            CaptureSession::Options options;
            options.window = window;
            if (!window)
            {
                HWND foreground = GetForegroundWindow();
                options.monitor = MonitorFromWindow(foreground ? foreground : GetDesktopWindow(), MONITOR_DEFAULTTOPRIMARY);
            }
            // 60 halves the capture's work on the GPU; 120 only on a screen that shows it.
            options.fps = std::min<UINT>(RefreshRate(window ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) : options.monitor), m_settings.maxFps);
            options.codec = m_support.Best();
            options.hevcFallback = m_support.hevc && m_support.hevcPlayback;
            options.quality = m_settings.quality;
            options.cursor = m_settings.cursor;
            options.gameHook = window && window == m_hookWindow && window != m_hookFailed;
            auto sink = [this](PacketPtr packet) { OnPacket(packet); };
            auto video = std::make_unique<CaptureSession>(m_gpu, options, sink);

            AudioCapture::Options audio;
            if (window) GetWindowThreadProcessId(window, &audio.gamePid);
            audio.discordPid = FindDiscord();
            std::unique_ptr<AudioCapture> sound;
            try { sound = std::make_unique<AudioCapture>(audio, sink); }
            catch (const std::exception& e) { Log(std::string("Sound off for this session: ") + e.what()); }

            StreamFormat format;
            format.video = video->VideoType();
            if (!format.video) throw std::runtime_error("the encoder gave no picture");
            if (sound) format.audio = sound->Types();
            format.fps = static_cast<int>(video->Fps());
            {
                UINT32 header = 0, items = 0;
                format.video->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &header);
                format.video->GetCount(&items);
                Log("Video type: " + std::to_string(items) + " attributes, sequence header " + std::to_string(header) +
                    " bytes; " + std::to_string(format.audio.size()) + " sound tracks.");
            }
            {
                std::lock_guard lock(m_stateLock);
                m_video = std::move(video);
                m_audio = std::move(sound);
                m_format = format;
                m_targetWindow = window;
                m_sessionStart = Now100ns();
                m_startedContent = { static_cast<LONG>(m_video->Width()), static_cast<LONG>(m_video->Height()) };
                auto content = m_video->ContentSize();
                if (content.cx > 0) m_startedContent = content;
                m_resizedTicks = 0;
                m_error.clear();
            }
            // Asking again holds the session thread for seconds on every restart, and fails the same way.
            if (options.gameHook && !m_video->FromGame()) m_hookFailed = window;
            Log("Capture started: " + std::to_string(m_video->Width()) + "x" + std::to_string(m_video->Height()) + " @" +
                std::to_string(m_video->Fps()) + " " + Narrow(CodecName(m_video->CodecUsed())) + " " +
                std::to_string(m_video->Bitrate() / 1000) + " kbit/s" + (m_video->FromGame() ? " (from inside the game)" : window ? " (game window)" : " (screen)"));
        }
        catch (const std::exception& e)
        {
            Log(std::string("Capture could not start: ") + e.what());
            std::lock_guard lock(m_stateLock);
            m_error = L"Capture could not start: " + Widen(e.what());
        }
        catch (winrt::hresult_error const& e)
        {
            Log("Capture could not start: " + Narrow(std::wstring(e.message())));
            std::lock_guard lock(m_stateLock);
            m_error = L"Capture could not start.";
        }
        PostState();
    }

    void StopSession()
    {
        std::unique_ptr<CaptureSession> video;
        std::unique_ptr<AudioCapture> audio;
        {
            std::lock_guard lock(m_stateLock);
            video = std::move(m_video);
            audio = std::move(m_audio);
            m_buffering = false;
        }
        bool had = video != nullptr;
        video.reset();
        audio.reset();
        m_buffer.Clear();
        if (had) { Log("Capture stopped."); PostState(); }
    }

    void SetBuffering(bool on)
    {
        bool changed;
        {
            std::lock_guard lock(m_stateLock);
            changed = m_buffering != on;
            m_buffering = on;
        }
        if (changed) PostState();
    }

    void OnPacket(const PacketPtr& packet)
    {
        if (m_buffering) m_buffer.Push(packet);
        std::lock_guard lock(m_recorderLock);
        if (m_recorder) m_recorder->Push(packet);
    }

    // ------------------------------------------------------------ saving

    // Session thread only: the game and the settings change there.
    std::wstring GameName() const { return m_gameWindow && !m_gameTitle.empty() ? SafeName(m_gameTitle) : L"Desktop"; }
    std::wstring GameId() const { return m_gameWindow ? m_gameId : L""; }

    /// Configure changes the settings under the state lock: other threads read a copy.
    Settings SettingsNow()
    {
        std::lock_guard lock(m_stateLock);
        return m_settings;
    }

    static std::wstring FolderFor(const std::wstring& root, const std::wstring& name)
    {
        std::wstring folder = root + L"\\" + name;
        if (root.empty() || !EnsureFolder(folder)) throw std::runtime_error("The captures folder can't be written.");
        return folder;
    }

    static void WriteThumb(const std::wstring& thumbs, const std::wstring& media, const std::vector<uint8_t>& pixels, UINT w, UINT h)
    {
        if (thumbs.empty() || pixels.empty()) return;
        try
        {
            EnsureFolder(thumbs);
            SaveImage(thumbs + L"\\" + ThumbName(media) + L".jpg", pixels.data(), w, h, w * 4, false, 480);
        }
        catch (const std::exception& e) { Log(std::string("Thumbnail: ") + e.what()); }
    }

    HMONITOR TargetMonitor() const
    {
        if (m_targetWindow && IsWindow(m_targetWindow)) return MonitorFromWindow(m_targetWindow, MONITOR_DEFAULTTONEAREST);
        return MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY);
    }

    /// game: the Exo game id it belongs to; empty lets Exo take it from the file's folder.
    static void Saved(const wchar_t* kind, const std::wstring& path, int64_t duration, const std::wstring& game)
    {
        auto o = Obj();
        o.Insert(L"kind", json::JsonValue::CreateStringValue(kind));
        o.Insert(L"path", json::JsonValue::CreateStringValue(path));
        o.Insert(L"durationMs", json::JsonValue::CreateNumberValue(duration / 10'000.0));
        o.Insert(L"game", json::JsonValue::CreateStringValue(game));
        Emit(L"saved", o);
    }

    void SaveReplay()
    {
        if (!m_video || !m_buffering)
        {
            m_ui.Toast(L"Instant replay is off", m_settings.replayMode == L"games" ? L"It runs while a game is open" : L"", TargetMonitor(), false);
            return;
        }
        auto clip = m_buffer.Snapshot(static_cast<int64_t>(m_settings.replaySeconds) * 10'000'000);
        if (!clip.Size()) { m_ui.Toast(L"Nothing to save yet", L"", TargetMonitor(), false); return; }
        std::vector<uint8_t> pixels;
        UINT w = 0, h = 0;
        m_video->LastFrame(pixels, w, h);
        auto format = m_format;
        auto name = GameName();
        auto game = GameId();
        auto folder = m_settings.folder, thumbs = m_settings.thumbs;
        auto monitor = TargetMonitor();
        // The file is written off the session thread, from the clip's own list (its files stay until it is done); the
        // buffer keeps filling.
        std::thread([this, clip = std::move(clip), pixels = std::move(pixels), w, h, format, name, game, folder, thumbs, monitor]() mutable {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            try
            {
                auto path = FreePath(FolderFor(folder, name), name + L" clip " + Stamp(), L".mp4");
                int64_t duration = 0;
                {
                    Mp4Writer writer(path, format);
                    for (size_t i = 0; i < clip.Size(); ++i) writer.Write(clip.Get(i));
                    duration = writer.Finish();
                }
                WriteThumb(thumbs, path, pixels, w, h);
                Saved(L"clip", path, duration, game);
                wchar_t detail[64];
                swprintf_s(detail, L"%d s", static_cast<int>((duration + 5'000'000) / 10'000'000));
                m_ui.Toast(L"Clip saved", detail, monitor);
            }
            catch (const std::exception& e)
            {
                Log(std::string("Replay save failed: ") + e.what());
                m_ui.Toast(L"Clip not saved", Widen(e.what()), monitor, false);
            }
        }).detach();
    }

    void ToggleRecording()
    {
        if (m_recorder) { StopRecording(true); return; }
        if (!m_video) StartSession(m_gameWindow);
        if (!m_video) { m_ui.Toast(L"Recording could not start", L"", TargetMonitor(), false); return; }
        try
        {
            auto name = GameName();
            auto path = FreePath(FolderFor(m_settings.folder, name), name + L" " + Stamp(), L".mp4");
            auto recorder = std::make_unique<Recorder>(path, m_format);
            {
                std::lock_guard lock(m_recorderLock);
                m_recorder = std::move(recorder);
            }
            m_video->ForceKeyFrame();
            {
                std::lock_guard lock(m_stateLock);
                m_recording = true;
                m_recordStart = Now100ns();
            }
            m_ui.Toast(L"Recording", L"Stop with " + m_settings.keys[1], TargetMonitor());
            Log("Recording started.");
        }
        catch (const std::exception& e)
        {
            Log(std::string("Recording failed: ") + e.what());
            m_ui.Toast(L"Recording could not start", Widen(e.what()), TargetMonitor(), false);
        }
        PostState();
    }

    void StopRecording(bool notify)
    {
        std::unique_ptr<Recorder> recorder;
        {
            std::lock_guard lock(m_recorderLock);
            recorder = std::move(m_recorder);
        }
        if (!recorder) return;
        {
            std::lock_guard lock(m_stateLock);
            m_recording = false;
        }
        int64_t duration = recorder->Stop();
        auto path = recorder->Path();
        auto error = recorder->Error();
        recorder.reset();
        if (!error.empty()) Log("Recording: " + error);
        std::vector<uint8_t> pixels;
        UINT w = 0, h = 0;
        if (m_video && m_video->LastFrame(pixels, w, h)) WriteThumb(m_settings.thumbs, path, pixels, w, h);
        Saved(L"recording", path, duration, GameId());
        Log("Recording saved: " + std::to_string(duration / 10'000'000) + " s.");
        if (notify)
        {
            int seconds = static_cast<int>(duration / 10'000'000);
            wchar_t detail[64];
            swprintf_s(detail, L"%d:%02d", seconds / 60, seconds % 60);
            m_ui.Toast(L"Recording saved", detail, TargetMonitor());
        }
        PostState();
        if (!m_quit) Tick();
    }

    void StartSnip()
    {
        m_ui.Snip([this](std::optional<SnipResult> result) {
            if (!result) return;
            auto snip = std::make_shared<SnipResult>(std::move(*result));
            // On the window thread: the clipboard wants a window of ours.
            CopyImageToClipboard(m_ui.Window(), snip->pixels.data(), snip->width, snip->height, snip->width * 4);
            // The snip opens in Exo's editor: we took the last click, so Exo may come to the front.
            AllowSetForegroundWindow(ASFW_ANY);
            m_jobs.Post([this, snip] {
                try
                {
                    auto name = GameName();
                    auto path = FreePath(FolderFor(m_settings.folder, name), name + L" " + Stamp(), L".png");
                    SaveImage(path, snip->pixels.data(), snip->width, snip->height, snip->width * 4, true);
                    WriteThumb(m_settings.thumbs, path, snip->pixels, snip->width, snip->height);
                    Saved(L"snip", path, 0, GameId());
                }
                catch (const std::exception& e)
                {
                    Log(std::string("Snip save failed: ") + e.what());
                    m_ui.Toast(L"Snip copied, not saved", Widen(e.what()), snip->monitor, false);
                }
            });
        });
    }

    void OnHotkey(int id)
    {
        if (id == SaveReplayKey) m_jobs.Post([this] { SaveReplay(); });
        else if (id == RecordKey) m_jobs.Post([this] { ToggleRecording(); });
        else if (id == SnipKey || id == PrintScreenKey) StartSnip();
    }

    // ------------------------------------------------------------ editing

    std::wstring Checked(const json::JsonObject& p, const wchar_t* key)
    {
        auto path = GetString(p, key);
        if (path.empty() || !IsUnder(path, SettingsNow().folder)) throw std::runtime_error("That file is not in the captures folder.");
        return FullPath(path);
    }

    json::IJsonValue Trim(const json::JsonObject& p)
    {
        auto input = Checked(p, L"path");
        int64_t start = static_cast<int64_t>(GetNumber(p, L"startMs", 0) * 10'000);
        int64_t end = static_cast<int64_t>(GetNumber(p, L"endMs", 0) * 10'000);
        auto dot = input.find_last_of(L'.');
        auto base = input.substr(0, dot);
        auto slash = base.find_last_of(L'\\');
        auto output = FreePath(base.substr(0, slash), base.substr(slash + 1) + L" trim", L".mp4");
        int64_t duration = TrimCopy(input, output, start, end);
        MakeThumb(SettingsNow().thumbs, output, start + (end > start ? (end - start) / 2 : 0));
        Saved(L"trim", output, duration, L"");
        auto o = Obj();
        o.Insert(L"path", json::JsonValue::CreateStringValue(output));
        o.Insert(L"durationMs", json::JsonValue::CreateNumberValue(duration / 10'000.0));
        return o;
    }

    json::IJsonValue Export(const json::JsonObject& p)
    {
        auto input = Checked(p, L"path");
        auto preset = GetString(p, L"preset", L"discord");
        int64_t start = static_cast<int64_t>(GetNumber(p, L"startMs", 0) * 10'000);
        int64_t end = static_cast<int64_t>(GetNumber(p, L"endMs", 0) * 10'000);
        auto dot = input.find_last_of(L'.');
        auto base = input.substr(0, dot);
        auto slash = base.find_last_of(L'\\');
        std::wstring suffix = preset == L"vertical" ? L" vertical" : preset == L"youtube" ? L" full" : L" for Discord";
        auto output = FreePath(base.substr(0, slash), base.substr(slash + 1) + suffix, L".mp4");
        int64_t duration = 0;
        if (preset == L"youtube") duration = TrimCopy(input, output, start, end);
        else
        {
            auto error = ExportClip(input, output, preset, start, end);
            if (!error.empty()) throw std::runtime_error(Narrow(Widen(error)));
            duration = MediaDuration(output);
        }
        MakeThumb(SettingsNow().thumbs, output, duration / 2);
        Saved(L"export", output, duration, L"");
        auto o = Obj();
        o.Insert(L"path", json::JsonValue::CreateStringValue(output));
        o.Insert(L"durationMs", json::JsonValue::CreateNumberValue(duration / 10'000.0));
        return o;
    }

    /// Re-encodes a finished recording smaller beside the original, which stays as it is (the caller decides what
    /// replaces what). One at a time; the result says why a file was left alone.
    json::IJsonValue ShrinkFile(const json::JsonObject& p)
    {
        auto input = Checked(p, L"input");
        ShrinkOptions options;
        options.input = input;
        options.codec = GetString(p, L"codec", L"auto");
        options.quality = static_cast<int>(std::clamp(GetNumber(p, L"targetQuality", ShrinkDefaultQuality), 0.0, 100.0));
        if (auto output = GetString(p, L"output"); !output.empty())
        {
            if (!IsUnder(output, SettingsNow().folder)) throw std::runtime_error("That file is not in the captures folder.");
            options.output = FullPath(output);
        }
        {
            std::lock_guard lock(m_shrinkLock);
            if (m_shrinking)
            {
                ShrinkResult busy;
                busy.input = input;
                busy.skipped = "busy";
                busy.detail = "another file is being shrunk";
                return ShrinkJson(busy);
            }
            m_shrinking = true;
            m_shrinkInput = Lower(input);
            m_shrinkCancel = false;
        }
        options.cancel = &m_shrinkCancel;
        options.progress = [input](double percent) {
            auto o = Obj();
            o.Insert(L"input", json::JsonValue::CreateStringValue(input));
            o.Insert(L"percent", json::JsonValue::CreateNumberValue(std::round(percent * 10) / 10));
            Emit(L"shrink", o);
        };
        ShrinkResult result;
        try { result = ::Shrink(options); }
        catch (...)
        {
            std::lock_guard lock(m_shrinkLock);
            m_shrinking = false;
            throw;
        }
        {
            std::lock_guard lock(m_shrinkLock);
            m_shrinking = false;
        }
        return ShrinkJson(result);
    }

    json::IJsonValue CancelShrink(const json::JsonObject& p)
    {
        auto input = GetString(p, L"input");
        std::lock_guard lock(m_shrinkLock);
        bool running = m_shrinking && (input.empty() || Lower(FullPath(input)) == m_shrinkInput);
        if (running) m_shrinkCancel = true;
        return json::JsonValue::CreateBooleanValue(running);
    }

    json::IJsonValue CopyFile(const json::JsonObject& p)
    {
        auto input = Checked(p, L"path");
        bool ok = false;
        m_ui.Invoke([&] { ok = CopyFileToClipboard(m_ui.Window(), input); });
        return json::JsonValue::CreateBooleanValue(ok);
    }

    /// What a saved video holds, for Exo's end-to-end check: its tracks, frames, key
    /// frames and each sound track's loudest sample (decoded).
    static json::IJsonValue Probe(const std::wstring& path)
    {
        auto o = Obj();
        o.Insert(L"durationMs", json::JsonValue::CreateNumberValue(MediaDuration(path) / 10'000.0));
        com_ptr<IMFSourceReader> reader;
        Check(MFCreateSourceReaderFromURL(path.c_str(), nullptr, reader.put()), "open");
        json::JsonArray tracks;
        for (DWORD i = 0;; ++i)
        {
            com_ptr<IMFMediaType> type;
            if (reader->GetNativeMediaType(i, 0, type.put()) == MF_E_INVALIDSTREAMNUMBER) break;
            GUID major{}, sub{};
            type->GetMajorType(&major);
            type->GetGUID(MF_MT_SUBTYPE, &sub);
            auto t = Obj();
            bool video = major == MFMediaType_Video;
            t.Insert(L"type", json::JsonValue::CreateStringValue(video ? L"video" : L"audio"));
            if (video)
            {
                UINT32 w = 0, h = 0;
                MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &w, &h);
                t.Insert(L"width", json::JsonValue::CreateNumberValue(w));
                t.Insert(L"height", json::JsonValue::CreateNumberValue(h));
                t.Insert(L"codec", json::JsonValue::CreateStringValue(sub == MFVideoFormat_HEVC ? L"HEVC" : sub == MFVideoFormat_H264 ? L"H.264" : sub == MFVideoFormat_AV1 ? L"AV1" : L"other"));
            }
            tracks.Append(t);
        }
        // Count frames without decoding; then decode sound for its level.
        std::vector<double> frames(tracks.Size()), keys(tracks.Size()), peaks(tracks.Size());
        for (DWORD i = 0; i < tracks.Size(); ++i)
        {
            com_ptr<IMFMediaType> native;
            reader->GetNativeMediaType(i, 0, native.put());
            bool video = std::wstring(tracks.GetObjectAt(i).GetNamedString(L"type")) == L"video";
            if (video) reader->SetCurrentMediaType(i, nullptr, native.get());
            else
            {
                com_ptr<IMFMediaType> pcm;
                MFCreateMediaType(pcm.put());
                pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                reader->SetCurrentMediaType(i, nullptr, pcm.get());
            }
            reader->SetStreamSelection(i, TRUE);
        }
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
                for (DWORD i = 0; i < tracks.Size(); ++i) { BOOL on = FALSE; reader->GetStreamSelection(i, &on); any |= on != FALSE; }
                if (!any) break;
                continue;
            }
            if (!sample || stream >= frames.size()) continue;
            frames[stream] += 1;
            if (MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, FALSE)) keys[stream] += 1;
            if (std::wstring(tracks.GetObjectAt(stream).GetNamedString(L"type")) == L"audio")
            {
                com_ptr<IMFMediaBuffer> buffer;
                sample->ConvertToContiguousBuffer(buffer.put());
                BYTE* data = nullptr;
                DWORD length = 0;
                buffer->Lock(&data, nullptr, &length);
                auto* pcm = reinterpret_cast<const int16_t*>(data);
                for (DWORD s = 0; s < length / 2; ++s) peaks[stream] = std::max<double>(peaks[stream], std::abs(pcm[s]) / 32768.0);
                buffer->Unlock();
            }
        }
        for (UINT32 i = 0; i < tracks.Size(); ++i)
        {
            auto t = tracks.GetObjectAt(i);
            t.Insert(L"samples", json::JsonValue::CreateNumberValue(frames[i]));
            t.Insert(L"keyFrames", json::JsonValue::CreateNumberValue(keys[i]));
            if (std::wstring(t.GetNamedString(L"type")) == L"audio") t.Insert(L"peak", json::JsonValue::CreateNumberValue(peaks[i]));
        }
        o.Insert(L"tracks", tracks);
        return o;
    }

    /// A thumbnail from the video itself: the frame at 'at', decoded by Windows.
    static bool MakeThumb(const std::wstring& thumbs, const std::wstring& path, int64_t at)
    {
        if (thumbs.empty()) return false;
        try
        {
            com_ptr<IMFAttributes> attrs;
            MFCreateAttributes(attrs.put(), 1);
            attrs->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
            com_ptr<IMFSourceReader> reader;
            Check(MFCreateSourceReaderFromURL(path.c_str(), attrs.get(), reader.put()), "open");
            const DWORD video = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
            reader->SetStreamSelection(video, TRUE);
            com_ptr<IMFMediaType> type;
            MFCreateMediaType(type.put());
            type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            Check(reader->SetCurrentMediaType(video, nullptr, type.get()), "decode");
            com_ptr<IMFMediaType> current;
            reader->GetCurrentMediaType(video, current.put());
            UINT32 w = 0, h = 0;
            MFGetAttributeSize(current.get(), MF_MT_FRAME_SIZE, &w, &h);
            PROPVARIANT pos;
            PropVariantInit(&pos);
            pos.vt = VT_I8;
            pos.hVal.QuadPart = std::max<int64_t>(0, at);
            reader->SetCurrentPosition(GUID_NULL, pos);
            for (int i = 0; i < 300; ++i)
            {
                DWORD stream = 0, flags = 0;
                LONGLONG time = 0;
                com_ptr<IMFSample> sample;
                Check(reader->ReadSample(video, 0, &stream, &flags, &time, sample.put()), "read");
                if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
                if (!sample || (time < at && i < 299)) continue;
                com_ptr<IMFMediaBuffer> buffer;
                sample->ConvertToContiguousBuffer(buffer.put());
                BYTE* data = nullptr;
                DWORD length = 0;
                buffer->Lock(&data, nullptr, &length);
                std::vector<uint8_t> pixels(data, data + std::min<size_t>(length, static_cast<size_t>(w) * h * 4));
                buffer->Unlock();
                if (pixels.size() < static_cast<size_t>(w) * h * 4) return false;
                // RGB32 from Media Foundation is bottom-up only with a negative stride; it is top-down here.
                WriteThumb(thumbs, path, pixels, w, h);
                return true;
            }
        }
        catch (const std::exception& e) { Log(std::string("Thumbnail of a video: ") + e.what()); }
        return false;
    }

    Ui m_ui;
    Jobs m_jobs;
    std::shared_ptr<GpuDevice> m_gpu;
    EncoderSupport m_support;
    Settings m_settings;
    bool m_configured = false;
    bool m_recovered = false;
    std::atomic<bool> m_quit{ false };
    std::thread m_ticker;

    std::wstring m_gameId, m_gameTitle, m_gameFolder;
    HWND m_gameWindow = nullptr;

    std::mutex m_stateLock;
    std::unique_ptr<CaptureSession> m_video;
    std::unique_ptr<AudioCapture> m_audio;
    StreamFormat m_format;
    HWND m_targetWindow = nullptr;
    HWND m_hookWindow = nullptr; // a game Windows capture gets no picture from
    HWND m_hookFailed = nullptr; // a game the hook did not take on
    int64_t m_sessionStart = 0;
    SIZE m_startedContent{};
    int m_resizedTicks = 0;
    std::atomic<bool> m_buffering{ false };
    bool m_recording = false;
    int64_t m_recordStart = 0;
    bool m_keyOk[3] = {};
    bool m_printOk = false;
    std::wstring m_error;
    ReplayBuffer m_buffer;

    std::mutex m_recorderLock;
    std::unique_ptr<Recorder> m_recorder;

    std::mutex m_shrinkLock;
    bool m_shrinking = false;
    std::atomic<bool> m_shrinkCancel{ false };
    std::wstring m_shrinkInput;

    std::mutex m_streamLock;
    std::unique_ptr<Streamer> m_stream;
    uint64_t m_streamId = 0;
    std::atomic<uint64_t> m_streamIds{ 0 };
};
}

int main()
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    Check(MFStartup(MF_VERSION), "Media Foundation");
    std::ios::sync_with_stdio(false);

    std::unique_ptr<Controller> controller;
    try { controller = std::make_unique<Controller>(); }
    catch (const std::exception& e)
    {
        Log(std::string("Exo Capture could not start: ") + e.what());
        auto o = Obj();
        o.Insert(L"message", json::JsonValue::CreateStringValue(L"Capture could not start on this PC's graphics card."));
        Emit(L"failed", o);
        return 2;
    }
    Emit(L"ready", controller->State());

    std::string line;
    while (std::getline(std::cin, line))
    {
        if (line.empty() || line.size() > 64 * 1024) continue;
        json::JsonObject request{ nullptr };
        if (!json::JsonObject::TryParse(Widen(line), request)) continue;
        double id = GetNumber(request, L"id", -1);
        auto method = GetString(request, L"method");
        if (method == L"shutdown") break;
        json::JsonObject params = request.HasKey(L"params") && request.Lookup(L"params").ValueType() == json::JsonValueType::Object
            ? request.GetNamedObject(L"params") : json::JsonObject();
        // Each request on its own thread: a trim or export must not hold up a save.
        std::thread([&controller, id, method, params] {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            try { Reply(id, controller->Handle(method, params)); }
            catch (const std::exception& e) { ReplyError(id, e.what()); }
            catch (winrt::hresult_error const& e) { ReplyError(id, Narrow(std::wstring(e.message()))); }
        }).detach();
    }
    // Finishes a recording in progress; requests still running end with the process.
    controller->Shutdown();
    fflush(stdout);
    ExitProcess(0);
}
