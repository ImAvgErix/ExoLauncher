// Exo Capture - its own small windows: hotkeys, the "Clip saved" toast and the snip overlay. Each is a
// separate top-level window; nothing is ever drawn into a game.
// GPL-3.0-or-later.
#pragma once
#include "common.h"

struct Hotkey
{
    UINT mods = 0;
    UINT vk = 0;
    bool Valid() const { return vk != 0; }
};
/// "Ctrl+Alt+F10" to a hotkey; empty or unknown text gives an invalid one.
Hotkey ParseHotkey(const std::wstring& text);

struct SnipResult
{
    std::vector<uint8_t> pixels; // BGRA, top-down
    UINT width = 0, height = 0;
    HMONITOR monitor = nullptr;
};

class Ui
{
public:
    /// Starts the window thread. onHotkey(id) runs on it; keep work short.
    explicit Ui(std::function<void(int)> onHotkey);
    ~Ui();

    /// Runs fn on the window thread and waits for it.
    void Invoke(const std::function<void()>& fn);
    /// Runs fn on the window thread later.
    void Post(std::function<void()> fn);

    /// Registers hotkey id (replacing it); false when another app already holds those keys.
    bool SetHotkey(int id, const Hotkey& key);
    void SetFont(const std::wstring& fontFile) { m_fontFile = fontFile; }

    /// A small topmost card at the top right of the monitor for a few seconds.
    void Toast(const std::wstring& title, const std::wstring& detail, HMONITOR monitor, bool good = true);
    /// Dims the screen; drag a rectangle, click a window, or Enter for the whole screen. done() gets nothing on Escape.
    void Snip(std::function<void(std::optional<SnipResult>)> done);

    HWND Window() const { return m_message; }
    struct Impl;

private:
    void Run();
    static LRESULT CALLBACK Proc(HWND, UINT, WPARAM, LPARAM);
    LRESULT Handle(HWND, UINT, WPARAM, LPARAM);

    std::unique_ptr<Impl> m;
    std::function<void(int)> m_onHotkey;
    std::wstring m_fontFile;
    HWND m_message = nullptr;
    DWORD m_threadId = 0;
    std::thread m_thread;
    std::mutex m_lock;
    std::deque<std::function<void()>> m_jobs;
    HANDLE m_ready = nullptr;
};
