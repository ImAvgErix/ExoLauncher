// Exo Capture - game capture through OBS Studio's graphics hook (OBS's own signed files, unchanged).
// GPL-3.0-or-later. The hook protocol follows OBS Studio's plugins/win-capture (GPL-2.0-or-later).
#pragma once
#include "common.h"

/// Copies a game's finished frames into a shared GPU texture from inside the game, so Windows
/// never has to compose the game for capture. Injected the way OBS's anti-cheat compatible
/// mode does (a Windows message hook on the game's window thread); never for Riot games or
/// while Riot Vanguard, FACEIT or ESEA runs, and never alongside another app (like OBS) already capturing it.
class GameHook
{
public:
    /// Hooks the game that owns window, copying at most once per intervalNs. Null when it
    /// cannot (the reason is logged); capture then goes through Windows instead.
    static std::unique_ptr<GameHook> Start(HWND window, uint64_t intervalNs, ID3D11Device* device);
    ~GameHook();

    /// The game's newest frame, reopened when the game resizes; null until the first one and
    /// once the hook stopped.
    ID3D11Texture2D* Frame();
    SIZE Size() const { return m_size; }

private:
    GameHook() = default;
    bool Open();
    void Close();

    ID3D11Device* m_device = nullptr;
    HANDLE m_process = nullptr;
    HANDLE m_keepalive = nullptr;
    HANDLE m_infoMap = nullptr;
    struct HookInfo* m_info = nullptr;
    HANDLE m_restart = nullptr, m_stop = nullptr, m_init = nullptr, m_ready = nullptr, m_exit = nullptr;
    HANDLE m_dataMap = nullptr;
    void* m_data = nullptr;
    com_ptr<ID3D11Texture2D> m_texture;
    SIZE m_size{};
    bool m_dead = false;
};
