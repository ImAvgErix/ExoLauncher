// Exo Capture - hotkeys, the toast and the snip overlay. GPL-3.0-or-later.
#include "ui.h"
#include "video.h"

#include <d2d1_1.h>
#include <dwrite_3.h>
#include <dwmapi.h>
#include <windowsx.h>

#include <algorithm>
#include <cwctype>
#include <map>

namespace
{
constexpr UINT WM_RUN = WM_APP + 1;
const D2D1_COLOR_F Black = { 0, 0, 0, 1 };
const D2D1_COLOR_F Ink = { 0xee / 255.f, 0xee / 255.f, 0xee / 255.f, 1 };
const D2D1_COLOR_F Muted = { 0x99 / 255.f, 0x99 / 255.f, 0x99 / 255.f, 1 };
const D2D1_COLOR_F Hairline = { 0x24 / 255.f, 0x24 / 255.f, 0x24 / 255.f, 1 };
const D2D1_COLOR_F Good = { 0x3d / 255.f, 0xd6 / 255.f, 0x8c / 255.f, 1 };
const D2D1_COLOR_F Bad = { 0xff / 255.f, 0x5c / 255.f, 0x5c / 255.f, 1 };

struct Win
{
    HWND hwnd = nullptr;
    virtual ~Win() = default;
    virtual LRESULT Handle(UINT msg, WPARAM wp, LPARAM lp) = 0;
};

LRESULT CALLBACK WinProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_NCCREATE)
    {
        auto* win = static_cast<Win*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
        win->hwnd = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(win));
    }
    auto* win = reinterpret_cast<Win*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (win)
    {
        LRESULT result = win->Handle(msg, wp, lp);
        if (msg == WM_NCDESTROY)
        {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            delete win;
        }
        return result;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void Rounded(HWND hwnd)
{
    DWM_WINDOW_CORNER_PREFERENCE corner = DWMWCP_ROUND;
    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(corner));
    COLORREF border = RGB(0x24, 0x24, 0x24);
    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, &border, sizeof(border));
}

RECT WorkArea(HMONITOR monitor)
{
    if (!monitor) monitor = MonitorFromPoint({ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info{ sizeof(info) };
    GetMonitorInfoW(monitor, &info);
    return info.rcWork;
}

float ScaleOf(HMONITOR monitor)
{
    UINT dx = 96, dy = 96;
    using Fn = HRESULT(WINAPI*)(HMONITOR, int, UINT*, UINT*);
    static auto fn = reinterpret_cast<Fn>(GetProcAddress(LoadLibraryW(L"shcore.dll"), "GetDpiForMonitor"));
    if (fn && monitor) fn(monitor, 0, &dx, &dy);
    return dx / 96.f;
}
}

// ---------------------------------------------------------------- shared drawing

constexpr float ToastText = 12.5f;

struct Ui::Impl
{
    com_ptr<ID2D1Factory> d2d;
    com_ptr<IDWriteFactory> dwrite;
    com_ptr<IDWriteFontCollection> fonts; // Inter, when Exo passed its font
    std::wstring family = L"Segoe UI Variable Text";
    Win* toast = nullptr;
    Win* snip = nullptr;
    std::map<int, Hotkey> hotkeys;

    void LoadFont(const std::wstring& file)
    {
        if (file.empty() || fonts) return;
        try
        {
            auto factory = dwrite.as<IDWriteFactory5>();
            HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
            if (h == INVALID_HANDLE_VALUE) return;
            LARGE_INTEGER size{};
            GetFileSizeEx(h, &size);
            std::vector<uint8_t> bytes(static_cast<size_t>(size.QuadPart));
            DWORD read = 0;
            ReadFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr);
            CloseHandle(h);
            com_ptr<IDWriteInMemoryFontFileLoader> loader;
            Check(factory->CreateInMemoryFontFileLoader(loader.put()), "font loader");
            Check(factory->RegisterFontFileLoader(loader.get()), "font loader");
            std::vector<uint8_t> plain;
            auto container = factory->AnalyzeContainerType(bytes.data(), static_cast<UINT32>(bytes.size()));
            if (container != DWRITE_CONTAINER_TYPE_UNKNOWN)
            {
                com_ptr<IDWriteFontFileStream> stream;
                Check(factory->UnpackFontFile(container, bytes.data(), static_cast<UINT32>(bytes.size()), stream.put()), "font unpack");
                UINT64 length = 0;
                stream->GetFileSize(&length);
                const void* fragment = nullptr;
                void* context = nullptr;
                Check(stream->ReadFileFragment(&fragment, 0, length, &context), "font read");
                plain.assign(static_cast<const uint8_t*>(fragment), static_cast<const uint8_t*>(fragment) + length);
                stream->ReleaseFileFragment(context);
            }
            else plain = std::move(bytes);
            com_ptr<IDWriteFontFile> fontFile;
            Check(loader->CreateInMemoryFontFileReference(factory.get(), plain.data(), static_cast<UINT32>(plain.size()), nullptr, fontFile.put()), "font file");
            com_ptr<IDWriteFontSetBuilder1> builder;
            Check(factory->CreateFontSetBuilder(builder.put()), "font set");
            Check(builder->AddFontFile(fontFile.get()), "font add");
            com_ptr<IDWriteFontSet> set;
            Check(builder->CreateFontSet(set.put()), "font set");
            com_ptr<IDWriteFontCollection1> collection;
            Check(factory->CreateFontCollectionFromFontSet(set.get(), collection.put()), "font collection");
            UINT32 index = 0;
            BOOL exists = FALSE;
            collection->FindFamilyName(L"Inter Variable", &index, &exists);
            if (!exists) return;
            fonts = collection;
            family = L"Inter Variable";
        }
        catch (const std::exception& e) { Log(std::string("Font: ") + e.what()); }
    }

    com_ptr<IDWriteTextFormat> Text(float size, DWRITE_FONT_WEIGHT weight = DWRITE_FONT_WEIGHT_NORMAL,
        DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_LEADING)
    {
        com_ptr<IDWriteTextFormat> format;
        dwrite->CreateTextFormat(family.c_str(), fonts.get(), weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
            size, L"en-us", format.put());
        if (!format) return nullptr;
        format->SetTextAlignment(align);
        format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        com_ptr<IDWriteInlineObject> ellipsis;
        dwrite->CreateEllipsisTrimmingSign(format.get(), ellipsis.put());
        DWRITE_TRIMMING trim{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
        format->SetTrimming(&trim, ellipsis.get());
        return format;
    }

    /// The width of a line of text at this size (DIPs), for windows that fit their words.
    float Measure(const std::wstring& text, float size)
    {
        auto format = Text(size);
        com_ptr<IDWriteTextLayout> layout;
        if (!format || FAILED(dwrite->CreateTextLayout(text.c_str(), static_cast<UINT32>(text.size()), format.get(), 4000, 100, layout.put()))) return 240;
        DWRITE_TEXT_METRICS metrics{};
        layout->GetMetrics(&metrics);
        return metrics.widthIncludingTrailingWhitespace;
    }

    com_ptr<ID2D1HwndRenderTarget> Target(HWND hwnd, float scale)
    {
        RECT rc;
        GetClientRect(hwnd, &rc);
        com_ptr<ID2D1HwndRenderTarget> target;
        auto props = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96.f * scale, 96.f * scale);
        d2d->CreateHwndRenderTarget(props, D2D1::HwndRenderTargetProperties(hwnd, D2D1::SizeU(rc.right, rc.bottom),
            D2D1_PRESENT_OPTIONS_IMMEDIATELY), target.put());
        return target;
    }
};

// ---------------------------------------------------------------- toast

namespace
{
// One line in a small capsule: a dot (green, or red when something failed) and the words.
struct ToastWin : Win
{
    Ui::Impl* ui;
    std::wstring line;
    bool good;
    float scale;
    com_ptr<ID2D1HwndRenderTarget> target;

    ToastWin(Ui::Impl* impl, std::wstring l, bool g, float s) : ui(impl), line(std::move(l)), good(g), scale(s) {}

    LRESULT Handle(UINT msg, WPARAM wp, LPARAM lp) override
    {
        switch (msg)
        {
        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            if (!target) target = ui->Target(hwnd, scale);
            if (target)
            {
                RECT rc;
                GetClientRect(hwnd, &rc);
                float w = rc.right / scale, h = rc.bottom / scale;
                target->BeginDraw();
                target->Clear(Black);
                com_ptr<ID2D1SolidColorBrush> brush;
                target->CreateSolidColorBrush(Hairline, brush.put());
                target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(0.5f, 0.5f, w - 0.5f, h - 0.5f), 8, 8), brush.get(), 1);
                brush->SetColor(good ? Good : Bad);
                target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(15, h / 2), 3.5f, 3.5f), brush.get());
                brush->SetColor(Ink);
                if (auto format = ui->Text(ToastText))
                    target->DrawText(line.c_str(), static_cast<UINT32>(line.size()), format.get(), D2D1::RectF(26, 0, w - 12, h), brush.get());
                if (target->EndDraw() == D2DERR_RECREATE_TARGET) target = nullptr;
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_TIMER:
            DestroyWindow(hwnd);
            return 0;
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        case WM_DESTROY:
            if (ui->toast == this) ui->toast = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
};

// ---------------------------------------------------------------- snip overlay

struct SnipWin : Win
{
    Ui::Impl* ui;
    std::function<void(std::optional<SnipResult>)> done;
    std::vector<uint8_t> pixels; // the whole virtual screen, frozen
    int vx = 0, vy = 0, vw = 0, vh = 0;
    std::vector<RECT> windows; // top-level windows, front to back, in client coordinates
    com_ptr<ID2D1HwndRenderTarget> target;
    com_ptr<ID2D1Bitmap> bitmap;
    bool dragging = false;
    POINT start{}, now{};
    RECT selection{};
    RECT hintScreen{};
    bool finished = false;

    RECT Normalized(POINT a, POINT b) const
    {
        return { std::min(a.x, b.x), std::min(a.y, b.y), std::max(a.x, b.x), std::max(a.y, b.y) };
    }

    RECT MonitorAt(POINT p) const
    {
        POINT screen{ p.x + vx, p.y + vy };
        MONITORINFO info{ sizeof(info) };
        GetMonitorInfoW(MonitorFromPoint(screen, MONITOR_DEFAULTTONEAREST), &info);
        RECT r = info.rcMonitor;
        OffsetRect(&r, -vx, -vy);
        return r;
    }

    RECT WindowAt(POINT p) const
    {
        for (auto& r : windows)
            if (PtInRect(&r, p)) return r;
        return MonitorAt(p);
    }

    void Finish(std::optional<RECT> rect)
    {
        if (finished) return;
        finished = true;
        std::optional<SnipResult> result;
        if (rect)
        {
            RECT r = *rect;
            r.left = std::clamp<LONG>(r.left, 0, vw);
            r.right = std::clamp<LONG>(r.right, 0, vw);
            r.top = std::clamp<LONG>(r.top, 0, vh);
            r.bottom = std::clamp<LONG>(r.bottom, 0, vh);
            if (r.right - r.left >= 2 && r.bottom - r.top >= 2)
            {
                SnipResult snip;
                snip.width = r.right - r.left;
                snip.height = r.bottom - r.top;
                snip.pixels.resize(static_cast<size_t>(snip.width) * snip.height * 4);
                for (UINT y = 0; y < snip.height; ++y)
                    memcpy(snip.pixels.data() + static_cast<size_t>(y) * snip.width * 4,
                        pixels.data() + (static_cast<size_t>(r.top + y) * vw + r.left) * 4, static_cast<size_t>(snip.width) * 4);
                POINT centre{ (r.left + r.right) / 2 + vx, (r.top + r.bottom) / 2 + vy };
                snip.monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
                result = std::move(snip);
            }
        }
        ShowWindow(hwnd, SW_HIDE);
        auto callback = done;
        DestroyWindow(hwnd);
        if (callback) callback(std::move(result));
    }

    LRESULT Handle(UINT msg, WPARAM wp, LPARAM lp) override
    {
        switch (msg)
        {
        case WM_ERASEBKGND:
            return 1; // the frozen screen covers it all; erasing to black would flash
        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            if (!target) { target = ui->Target(hwnd, 1.f); bitmap = nullptr; }
            if (target)
            {
                if (!bitmap)
                    target->CreateBitmap(D2D1::SizeU(vw, vh), pixels.data(), vw * 4,
                        D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE)), bitmap.put());
                target->BeginDraw();
                if (bitmap) target->DrawBitmap(bitmap.get(), D2D1::RectF(0, 0, float(vw), float(vh)), 1.f, D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR);
                com_ptr<ID2D1SolidColorBrush> dim;
                target->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.55f), dim.put());
                RECT s = selection;
                float l = float(s.left), t = float(s.top), r = float(s.right), b = float(s.bottom);
                target->FillRectangle(D2D1::RectF(0, 0, float(vw), t), dim.get());
                target->FillRectangle(D2D1::RectF(0, b, float(vw), float(vh)), dim.get());
                target->FillRectangle(D2D1::RectF(0, t, l, b), dim.get());
                target->FillRectangle(D2D1::RectF(r, t, float(vw), b), dim.get());
                // Until a drag starts, how it works, in a pill at the top of the screen under the pointer
                // (macOS and the Snipping Tool say it the same way).
                if (!dragging)
                {
                    RECT m = MonitorAt(now);
                    float scale = ScaleOf(MonitorFromPoint({ now.x + vx, now.y + vy }, MONITOR_DEFAULTTONEAREST));
                    const wchar_t* hint = L"Drag to snip  \u00B7  Click a window  \u00B7  Enter for the whole screen  \u00B7  Esc to cancel";
                    float w = 520 * scale, h = 36 * scale;
                    float cx = (m.left + m.right) / 2.f;
                    D2D1_RECT_F pill = D2D1::RectF(cx - w / 2, m.top + 24 * scale, cx + w / 2, m.top + 24 * scale + h);
                    com_ptr<ID2D1SolidColorBrush> paint;
                    target->CreateSolidColorBrush(D2D1::ColorF(0.04f, 0.04f, 0.04f, 0.92f), paint.put());
                    target->FillRoundedRectangle(D2D1::RoundedRect(pill, h / 2, h / 2), paint.get());
                    paint->SetColor(Hairline);
                    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(pill.left + 0.5f, pill.top + 0.5f, pill.right - 0.5f, pill.bottom - 0.5f), h / 2, h / 2), paint.get(), 1);
                    paint->SetColor(Ink);
                    if (auto format = ui->Text(13 * scale, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_CENTER))
                        target->DrawText(hint, static_cast<UINT32>(wcslen(hint)), format.get(), pill, paint.get());
                }
                if (r > l && b > t)
                {
                    com_ptr<ID2D1SolidColorBrush> line;
                    // A dark line just outside the cream one keeps the edge visible over bright pictures.
                    target->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.6f), line.put());
                    target->DrawRectangle(D2D1::RectF(l - 0.5f, t - 0.5f, r + 0.5f, b + 0.5f), line.get(), 1);
                    line->SetColor(Ink);
                    target->DrawRectangle(D2D1::RectF(l + 0.5f, t + 0.5f, r - 0.5f, b - 0.5f), line.get(), 1);
                    // Its size, in a small black pill above (or inside) the corner.
                    wchar_t text[32];
                    swprintf_s(text, L"%ld × %ld", s.right - s.left, s.bottom - s.top);
                    float scale = ScaleOf(MonitorFromPoint({ s.left + vx, s.top + vy }, MONITOR_DEFAULTTONEAREST));
                    auto format = ui->Text(12 * scale, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_CENTER);
                    float w = 92 * scale, h = 24 * scale;
                    float y = t - h - 6 * scale >= 0 ? t - h - 6 * scale : t + 6 * scale;
                    D2D1_RECT_F pill = D2D1::RectF(l, y, l + w, y + h);
                    com_ptr<ID2D1SolidColorBrush> fill;
                    target->CreateSolidColorBrush(Black, fill.put());
                    target->FillRoundedRectangle(D2D1::RoundedRect(pill, h / 2, h / 2), fill.get());
                    if (format) target->DrawText(text, static_cast<UINT32>(wcslen(text)), format.get(), pill, line.get());
                }
                if (target->EndDraw() == D2DERR_RECREATE_TARGET) target = nullptr;
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_LBUTTONDOWN:
            dragging = true;
            start = now = { GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
            SetCapture(hwnd);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        case WM_MOUSEMOVE:
        {
            now = { GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
            RECT next = dragging && (std::abs(now.x - start.x) > 4 || std::abs(now.y - start.y) > 4) ? Normalized(start, now) : WindowAt(now);
            RECT screen = MonitorAt(now);
            if (!EqualRect(&next, &selection) || !EqualRect(&screen, &hintScreen))
            {
                selection = next;
                hintScreen = screen;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            return 0;
        }
        case WM_LBUTTONUP:
        {
            ReleaseCapture();
            POINT end{ GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
            bool moved = std::abs(end.x - start.x) > 4 || std::abs(end.y - start.y) > 4;
            dragging = false;
            Finish(moved ? Normalized(start, end) : WindowAt(end));
            return 0;
        }
        case WM_RBUTTONUP:
            Finish(std::nullopt);
            return 0;
        case WM_KEYDOWN:
            if (wp == VK_ESCAPE) Finish(std::nullopt);
            else if (wp == VK_RETURN)
            {
                POINT p;
                GetCursorPos(&p);
                Finish(MonitorAt({ p.x - vx, p.y - vy }));
            }
            return 0;
        case WM_SETCURSOR:
            SetCursor(LoadCursorW(nullptr, IDC_CROSS));
            return TRUE;
        case WM_KILLFOCUS:
            return 0;
        case WM_DESTROY:
            if (ui->snip == this) ui->snip = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
};

/// Top-level windows a click can pick, front to back, as their visible frame.
std::vector<RECT> PickableWindows(int vx, int vy)
{
    std::vector<RECT> out;
    struct Ctx { std::vector<RECT>* out; int vx, vy; } ctx{ &out, vx, vy };
    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto* c = reinterpret_cast<Ctx*>(lp);
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return TRUE;
        DWORD cloaked = 0;
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
        if (cloaked) return TRUE;
        wchar_t cls[64];
        GetClassNameW(hwnd, cls, 64);
        if (!wcscmp(cls, L"Progman") || !wcscmp(cls, L"WorkerW")) return TRUE;
        LONG ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if (ex & WS_EX_TRANSPARENT) return TRUE;
        RECT r{};
        if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof(r)))) GetWindowRect(hwnd, &r);
        if (r.right - r.left < 8 || r.bottom - r.top < 8) return TRUE;
        OffsetRect(&r, -c->vx, -c->vy);
        c->out->push_back(r);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return out;
}
}

// ---------------------------------------------------------------- hotkeys

Hotkey ParseHotkey(const std::wstring& text)
{
    Hotkey key;
    std::wstring part;
    std::vector<std::wstring> parts;
    for (wchar_t c : text + L"+")
    {
        if (c == L'+') { if (!part.empty()) parts.push_back(part); part.clear(); }
        else if (c != L' ') part += static_cast<wchar_t>(std::towlower(c));
    }
    for (auto& p : parts)
    {
        if (p == L"ctrl" || p == L"control") key.mods |= MOD_CONTROL;
        else if (p == L"alt") key.mods |= MOD_ALT;
        else if (p == L"shift") key.mods |= MOD_SHIFT;
        else if (p == L"win") key.mods |= MOD_WIN;
        else if (p.size() == 1 && std::iswalnum(p[0])) key.vk = std::towupper(p[0]);
        else if (p[0] == L'f' && p.size() <= 3 && std::iswdigit(p[1]))
        {
            int n = _wtoi(p.c_str() + 1);
            if (n >= 1 && n <= 24) key.vk = VK_F1 + n - 1;
        }
        else if (p == L"printscreen" || p == L"prtsc") key.vk = VK_SNAPSHOT;
        else if (p == L"space") key.vk = VK_SPACE;
        else if (p == L"home") key.vk = VK_HOME;
        else if (p == L"end") key.vk = VK_END;
        else if (p == L"insert") key.vk = VK_INSERT;
        else if (p == L"pageup") key.vk = VK_PRIOR;
        else if (p == L"pagedown") key.vk = VK_NEXT;
        else if (p == L"pause") key.vk = VK_PAUSE;
        else if (p == L"scrolllock") key.vk = VK_SCROLL;
        else if (p.rfind(L"num", 0) == 0 && p.size() == 4 && std::iswdigit(p[3])) key.vk = VK_NUMPAD0 + (p[3] - L'0');
        else return {};
    }
    return key;
}

// ---------------------------------------------------------------- the window thread

Ui::Ui(std::function<void(int)> onHotkey) : m(std::make_unique<Impl>()), m_onHotkey(std::move(onHotkey))
{
    m_ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    m_thread = std::thread([this] { Run(); });
    WaitForSingleObject(m_ready, 10000);
}

Ui::~Ui()
{
    if (m_threadId) PostThreadMessageW(m_threadId, WM_QUIT, 0, 0);
    if (m_thread.joinable()) m_thread.join();
    CloseHandle(m_ready);
}

void Ui::Post(std::function<void()> fn)
{
    {
        std::lock_guard lock(m_lock);
        m_jobs.push_back(std::move(fn));
    }
    PostMessageW(m_message, WM_RUN, 0, 0);
}

void Ui::Invoke(const std::function<void()>& fn)
{
    if (GetCurrentThreadId() == m_threadId) { fn(); return; }
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Post([&] { fn(); SetEvent(done); });
    WaitForSingleObject(done, 15000);
    CloseHandle(done);
}

LRESULT CALLBACK Ui::Proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_NCCREATE)
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams));
    auto* self = reinterpret_cast<Ui*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    return self ? self->Handle(hwnd, msg, wp, lp) : DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT Ui::Handle(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_RUN)
    {
        std::deque<std::function<void()>> jobs;
        {
            std::lock_guard lock(m_lock);
            jobs.swap(m_jobs);
        }
        for (auto& job : jobs)
        {
            try { job(); }
            catch (const std::exception& e) { Log(std::string("Window job: ") + e.what()); }
        }
        return 0;
    }
    if (msg == WM_HOTKEY)
    {
        if (m_onHotkey) m_onHotkey(static_cast<int>(wp));
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void Ui::Run()
{
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    m_threadId = GetCurrentThreadId();
    D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, m->d2d.put());
    DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<::IUnknown**>(m->dwrite.put()));

    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW message{ sizeof(message) };
    message.lpfnWndProc = Proc;
    message.hInstance = instance;
    message.lpszClassName = L"ExoCaptureMessages";
    RegisterClassExW(&message);
    WNDCLASSEXW popup{ sizeof(popup) };
    popup.lpfnWndProc = WinProc;
    popup.hInstance = instance;
    popup.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    popup.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    popup.lpszClassName = L"ExoCapturePopup";
    RegisterClassExW(&popup);
    m_message = CreateWindowExW(0, L"ExoCaptureMessages", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, this);
    SetEvent(m_ready);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    for (auto& [id, key] : m->hotkeys) UnregisterHotKey(m_message, id);
    DestroyWindow(m_message);
}

bool Ui::SetHotkey(int id, const Hotkey& key)
{
    bool ok = false;
    Invoke([&] {
        UnregisterHotKey(m_message, id);
        m->hotkeys.erase(id);
        if (!key.Valid()) { ok = true; return; }
        ok = RegisterHotKey(m_message, id, key.mods | MOD_NOREPEAT, key.vk) != FALSE;
        if (ok) m->hotkeys[id] = key;
    });
    return ok;
}

// A small one-line capsule at the top middle of the game's screen, there for a moment: the old card sat in the top-right
// corner, over a shooter's minimap or kill feed, for nearly three seconds. Done goes after 1.5 s; a failure stays 3.5 s to
// be read. Clicks go through it and it never takes focus.
void Ui::Toast(const std::wstring& title, const std::wstring& detail, HMONITOR monitor, bool good)
{
    Post([=] {
        m->LoadFont(m_fontFile);
        if (m->toast) DestroyWindow(m->toast->hwnd);
        float scale = ScaleOf(monitor ? monitor : MonitorFromPoint({ 0, 0 }, MONITOR_DEFAULTTOPRIMARY));
        RECT work = WorkArea(monitor);
        std::wstring line = detail.empty() ? title : title + L" \u00B7 " + detail;
        float text = std::min(m->Measure(line, ToastText), 420.f);
        int w = static_cast<int>((26 + text + 12) * scale + 1), h = static_cast<int>(28 * scale);
        auto* win = new ToastWin(m.get(), line, good, scale);
        HWND hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, L"ExoCapturePopup", L"Exo Capture",
            WS_POPUP, (work.left + work.right - w) / 2, work.top + static_cast<int>(10 * scale), w, h, nullptr, nullptr, GetModuleHandleW(nullptr), win);
        if (!hwnd) { delete win; return; }
        m->toast = win;
        Rounded(hwnd);
        SetTimer(hwnd, 1, good ? 1500 : 3500, nullptr);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    });
}


void Ui::Snip(std::function<void(std::optional<SnipResult>)> done)
{
    Post([=] {
        m->LoadFont(m_fontFile);
        if (m->snip) return; // one at a time
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        auto* win = new SnipWin();
        win->ui = m.get();
        win->done = done;
        win->vx = vx; win->vy = vy; win->vw = vw; win->vh = vh;
        win->windows = PickableWindows(vx, vy);

        // Freeze the screen as it is now (Exo's own windows included, the overlay not yet shown).
        HDC screen = GetDC(nullptr);
        HDC memory = CreateCompatibleDC(screen);
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = vw;
        info.bmiHeader.biHeight = -vh;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        void* bits = nullptr;
        HBITMAP dib = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        auto old = SelectObject(memory, dib);
        BitBlt(memory, 0, 0, vw, vh, screen, vx, vy, SRCCOPY | CAPTUREBLT);
        GdiFlush();
        win->pixels.assign(static_cast<uint8_t*>(bits), static_cast<uint8_t*>(bits) + static_cast<size_t>(vw) * vh * 4);
        for (size_t i = 3; i < win->pixels.size(); i += 4) win->pixels[i] = 255;
        SelectObject(memory, old);
        DeleteObject(dib);
        DeleteDC(memory);
        ReleaseDC(nullptr, screen);
        // On an HDR screen that copy is not what you saw: there, its own picture tone-mapped to SDR takes its place.
        EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR monitor, HDC, LPRECT, LPARAM lp) -> BOOL {
            auto* w = reinterpret_cast<SnipWin*>(lp);
            std::vector<uint8_t> hdr;
            RECT r{};
            if (!CaptureHdrMonitor(monitor, hdr, r)) return TRUE;
            LONG left = r.left - w->vx, top = r.top - w->vy, mw = r.right - r.left, mh = r.bottom - r.top;
            if (left < 0 || top < 0 || left + mw > w->vw || top + mh > w->vh) return TRUE;
            for (LONG y = 0; y < mh; ++y)
                memcpy(w->pixels.data() + (static_cast<size_t>(top + y) * w->vw + left) * 4,
                    hdr.data() + static_cast<size_t>(y) * mw * 4, static_cast<size_t>(mw) * 4);
            return TRUE;
        }, reinterpret_cast<LPARAM>(win));

        HWND hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"ExoCapturePopup", L"Exo Capture snip",
            WS_POPUP, vx, vy, vw, vh, nullptr, nullptr, GetModuleHandleW(nullptr), win);
        if (!hwnd) { delete win; done(std::nullopt); return; }
        m->snip = win;
        POINT p;
        GetCursorPos(&p);
        win->selection = win->WindowAt({ p.x - vx, p.y - vy });
        // Out of sight until its first frame (the frozen screen) is drawn: shown first, the window's own black
        // background would flash over every screen for a frame.
        BOOL cloak = TRUE;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &cloak, sizeof(cloak));
        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);
        DwmFlush();
        cloak = FALSE;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &cloak, sizeof(cloak));
        SetForegroundWindow(hwnd);
        SetFocus(hwnd);
    });
}
