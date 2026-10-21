// Exo Capture - game capture through OBS Studio's graphics hook.
// GPL-3.0-or-later. The hook protocol follows OBS Studio's plugins/win-capture (GPL-2.0-or-later).
#include "hook.h"

#include <tlhelp32.h>

#include <algorithm>
#include <cwctype>
#include <map>

// ---------------------------------------------------------------- OBS's hook protocol (graphics-hook-info.h)

#pragma pack(push, 8)
struct HookOffsets
{
    uint32_t d3d8Present;
    uint32_t d3d9Present, d3d9PresentEx, d3d9PresentSwap, d3d9ClassOffset, d3d9IsExClassOffset;
    uint32_t dxgiPresent, dxgiResize, dxgiPresent1;
    uint32_t ddraw[8];
    uint32_t dxgiRelease;
    uint32_t d3d12ExecuteCommandLists;
};

struct HookInfo
{
    uint32_t verMajor, verMinor;
    int type; // 0 memory, 1 shared texture
    uint32_t window, format, cx, cy, unusedBaseCx, unusedBaseCy, pitch, mapId, mapSize;
    bool flip;
    uint64_t frameInterval;
    bool unusedScale, forceShmem, captureOverlay, allowSrgbAlias;
    HookOffsets offsets;
    uint32_t reserved[126];
};
#pragma pack(pop)
static_assert(sizeof(HookInfo) == 648, "OBS hook_info ABI");

namespace
{
constexpr uint32_t HookMajor = 1;
constexpr int TypeTexture = 1;

std::wstring Named(const wchar_t* name, DWORD id) { return name + std::to_wstring(id); }

std::wstring LowerText(std::wstring text)
{
    std::transform(text.begin(), text.end(), text.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    return text;
}

std::wstring HookDir()
{
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring dir = path;
    return dir.substr(0, dir.find_last_of(L'\\') + 1) + L"obs\\";
}

std::wstring ImageOf(HANDLE process)
{
    wchar_t path[MAX_PATH]{};
    DWORD size = MAX_PATH;
    return QueryFullProcessImageNameW(process, 0, path, &size) ? std::wstring(path, size) : L"";
}

/// Anti-cheat that rejects any hook: a Riot game, and Riot Vanguard, FACEIT or ESEA while it runs.
std::string Refused(const std::wstring& image)
{
    auto lower = LowerText(image);
    if (lower.find(L"\\riot games\\") != std::wstring::npos || lower.find(L"valorant") != std::wstring::npos ||
        lower.find(L"league of legends") != std::wstring::npos)
        return "a Riot game";
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return {};
    std::string found;
    PROCESSENTRY32W entry{ sizeof(entry) };
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok && found.empty(); ok = Process32NextW(snapshot, &entry))
    {
        auto name = LowerText(entry.szExeFile);
        if (name == L"vgc.exe" || name == L"vgtray.exe") found = "Riot Vanguard is running";
        else if (name.rfind(L"faceit", 0) == 0) found = "FACEIT is running";
        else if (name.rfind(L"esea", 0) == 0) found = "ESEA is running";
    }
    CloseHandle(snapshot);
    return found;
}

bool IsAppContainer(HANDLE process)
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return false;
    DWORD value = 0, size = 0;
    bool app = GetTokenInformation(token, TokenIsAppContainer, &value, sizeof(value), &size) && value != 0;
    CloseHandle(token);
    return app;
}

/// Runs a helper and returns its exit code (and what it printed); -1 when it did not start or finish.
int RunHelper(const std::wstring& exe, const std::wstring& args, std::string* output, DWORD waitMs)
{
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, TRUE };
    HANDLE readPipe = nullptr, writePipe = nullptr;
    if (output && !CreatePipe(&readPipe, &writePipe, &sa, 0)) return -1;
    if (readPipe) SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW si{ sizeof(si) };
    if (output)
    {
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = writePipe;
        si.hStdError = writePipe;
    }
    PROCESS_INFORMATION pi{};
    std::wstring line = L"\"" + exe + L"\" " + args;
    BOOL started = CreateProcessW(exe.c_str(), line.data(), nullptr, nullptr, output != nullptr, CREATE_NO_WINDOW,
        nullptr, nullptr, &si, &pi);
    if (writePipe) CloseHandle(writePipe);
    if (!started)
    {
        if (readPipe) CloseHandle(readPipe);
        return -1;
    }
    CloseHandle(pi.hThread);
    if (output)
    {
        char buffer[4096];
        DWORD read = 0;
        while (ReadFile(readPipe, buffer, sizeof(buffer), &read, nullptr) && read > 0) output->append(buffer, read);
        CloseHandle(readPipe);
    }
    DWORD code = static_cast<DWORD>(-1);
    if (WaitForSingleObject(pi.hProcess, waitMs) == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &code);
    else TerminateProcess(pi.hProcess, 1);
    CloseHandle(pi.hProcess);
    return static_cast<int>(code);
}

/// Where Present and friends sit in this PC's graphics DLLs, from OBS's get-graphics-offsets, once per bitness.
bool Offsets(bool is64, HookOffsets& out)
{
    static std::mutex lock;
    static std::map<bool, std::pair<bool, HookOffsets>> cache;
    std::lock_guard guard(lock);
    auto found = cache.find(is64);
    if (found != cache.end())
    {
        out = found->second.second;
        return found->second.first;
    }
    std::string text;
    HookOffsets offsets{};
    bool ok = RunHelper(HookDir() + (is64 ? L"get-graphics-offsets64.exe" : L"get-graphics-offsets32.exe"), L"", &text, 10'000) == 0;
    std::string section;
    size_t at = 0;
    while (ok && at < text.size())
    {
        size_t end = text.find('\n', at);
        std::string line = text.substr(at, end == std::string::npos ? std::string::npos : end - at);
        at = end == std::string::npos ? text.size() : end + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
        if (line.size() > 2 && line.front() == '[') { section = line.substr(1, line.size() - 2); continue; }
        auto eq = line.find('=');
        if (eq == std::string::npos) continue;
        auto key = line.substr(0, eq);
        auto value = static_cast<uint32_t>(std::strtoul(line.c_str() + eq + 1, nullptr, 0));
        if (section == "d3d8" && key == "present") offsets.d3d8Present = value;
        else if (section == "d3d9" && key == "present") offsets.d3d9Present = value;
        else if (section == "d3d9" && key == "present_ex") offsets.d3d9PresentEx = value;
        else if (section == "d3d9" && key == "present_swap") offsets.d3d9PresentSwap = value;
        else if (section == "d3d9" && key == "d3d9_clsoff") offsets.d3d9ClassOffset = value;
        else if (section == "d3d9" && key == "is_d3d9ex_clsoff") offsets.d3d9IsExClassOffset = value;
        else if (section == "dxgi" && key == "present") offsets.dxgiPresent = value;
        else if (section == "dxgi" && key == "present1") offsets.dxgiPresent1 = value;
        else if (section == "dxgi" && key == "resize") offsets.dxgiResize = value;
        else if (section == "dxgi" && key == "release") offsets.dxgiRelease = value;
    }
    ok = ok && offsets.dxgiPresent != 0;
    if (!ok) Log("Game hook: no graphics offsets from OBS's helper.");
    cache[is64] = { ok, offsets };
    out = offsets;
    return ok;
}

HANDLE OpenEventFor(const wchar_t* name, DWORD pid)
{
    return OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE, Named(name, pid).c_str());
}

bool Signalled(HANDLE handle) { return handle && WaitForSingleObject(handle, 0) == WAIT_OBJECT_0; }

void CloseOne(HANDLE& handle)
{
    if (handle) CloseHandle(handle);
    handle = nullptr;
}
}

std::unique_ptr<GameHook> GameHook::Start(HWND window, uint64_t intervalNs, ID3D11Device* device)
{
    DWORD pid = 0;
    DWORD thread = GetWindowThreadProcessId(window, &pid);
    if (!thread || !pid) return nullptr;
    auto dir = HookDir();
    if (GetFileAttributesW((dir + L"graphics-hook64.dll").c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        Log("Game hook: OBS's hook files are not installed.");
        return nullptr;
    }

    std::unique_ptr<GameHook> hook(new GameHook());
    hook->m_device = device;
    hook->m_process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid);
    if (!hook->m_process) { Log("Game hook: cannot open the game's process."); return nullptr; }
    auto image = ImageOf(hook->m_process);
    if (auto why = Refused(image); !why.empty()) { Log("Game hook: not used, " + why + "."); return nullptr; }
    if (IsAppContainer(hook->m_process)) { Log("Game hook: not used for a Store app."); return nullptr; }
    USHORT processMachine = 0, nativeMachine = 0;
    bool is64 = !IsWow64Process2(hook->m_process, &processMachine, &nativeMachine) || processMachine == IMAGE_FILE_MACHINE_UNKNOWN;
    HookOffsets offsets{};
    if (!Offsets(is64, offsets)) return nullptr;

    // Someone else (OBS) already captures this game: its hook serves one app at a time.
    auto keepaliveName = Named(L"CaptureHook_KeepAlive", pid);
    if (HANDLE other = OpenMutexW(SYNCHRONIZE, FALSE, keepaliveName.c_str()))
    {
        CloseHandle(other);
        Log("Game hook: another app already captures this game.");
        return nullptr;
    }
    hook->m_keepalive = CreateMutexW(nullptr, FALSE, keepaliveName.c_str());
    if (!hook->m_keepalive) return nullptr;

    hook->m_restart = OpenEventFor(L"CaptureHook_Restart", pid);
    if (hook->m_restart) SetEvent(hook->m_restart); // already hooked earlier: start it again
    else
    {
        // OBS's anti-cheat compatible injection: a message hook on the game's window thread.
        std::wstring args = L"\"" + dir + (is64 ? L"graphics-hook64.dll" : L"graphics-hook32.dll") + L"\" 1 " + std::to_wstring(thread);
        int code = RunHelper(dir + (is64 ? L"inject-helper64.exe" : L"inject-helper32.exe"), args, nullptr, 10'000);
        if (code != 0) { Log("Game hook: injection did not take (" + std::to_string(code) + ")."); return nullptr; }
    }

    // The hook makes its objects as it loads.
    for (int i = 0; i < 50 && !hook->m_infoMap; ++i)
    {
        hook->m_infoMap = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, Named(L"CaptureHook_HookInfo", pid).c_str());
        if (!hook->m_infoMap) Sleep(100);
    }
    if (!hook->m_infoMap) { Log("Game hook: the hook did not load in the game."); return nullptr; }
    hook->m_info = static_cast<HookInfo*>(MapViewOfFile(hook->m_infoMap, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HookInfo)));
    if (!hook->m_info) return nullptr;
    hook->m_info->offsets = offsets;
    hook->m_info->frameInterval = intervalNs;
    hook->m_info->captureOverlay = false;
    hook->m_info->forceShmem = false;
    hook->m_info->unusedScale = false;
    hook->m_info->allowSrgbAlias = true;

    if (!hook->m_restart) hook->m_restart = OpenEventFor(L"CaptureHook_Restart", pid);
    hook->m_stop = OpenEventFor(L"CaptureHook_Stop", pid);
    hook->m_init = OpenEventFor(L"CaptureHook_Initialize", pid);
    hook->m_ready = OpenEventFor(L"CaptureHook_HookReady", pid);
    hook->m_exit = OpenEventFor(L"CaptureHook_Exit", pid);
    if (!hook->m_restart || !hook->m_stop || !hook->m_init || !hook->m_ready || !hook->m_exit)
    {
        Log("Game hook: its signals are missing.");
        return nullptr;
    }
    SetEvent(hook->m_init);

    // The first frame comes with the game's next Present.
    for (int i = 0; i < 40 && !hook->m_texture; ++i)
    {
        if (WaitForSingleObject(hook->m_ready, 100) == WAIT_OBJECT_0 && !hook->Open()) return nullptr;
        if (Signalled(hook->m_process)) return nullptr;
    }
    if (!hook->m_texture) { Log("Game hook: no frame from the game (it may draw with Vulkan or OpenGL)."); return nullptr; }
    Log("Game hook: capturing " + Narrow(image.substr(image.find_last_of(L'\\') + 1)) + " " +
        std::to_string(hook->m_size.cx) + "x" + std::to_string(hook->m_size.cy) + " from inside the game.");
    return hook;
}

bool GameHook::Open()
{
    if (!m_info) return false;
    if (m_info->verMajor > HookMajor) { Log("Game hook: a newer hook than Exo knows."); return false; }
    if (m_info->type != TypeTexture) { Log("Game hook: the game is on another graphics card."); return false; }
    m_texture = nullptr;
    if (m_data) UnmapViewOfFile(m_data);
    m_data = nullptr;
    CloseOne(m_dataMap);
    auto mapName = [&](uint32_t window) {
        return L"CaptureHook_Texture_" + std::to_wstring(static_cast<uint64_t>(window)) + L"_" + std::to_wstring(m_info->mapId);
    };
    m_dataMap = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, mapName(m_info->window).c_str());
    if (!m_dataMap) return false;
    m_data = MapViewOfFile(m_dataMap, FILE_MAP_ALL_ACCESS, 0, 0, m_info->mapSize);
    if (!m_data) return false;
    auto handle = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(*static_cast<uint32_t*>(m_data)));
    if (FAILED(m_device->OpenSharedResource(handle, IID_PPV_ARGS(m_texture.put()))))
    {
        Log("Game hook: cannot open the game's frame.");
        return false;
    }
    m_size = { static_cast<LONG>(m_info->cx), static_cast<LONG>(m_info->cy) };
    return true;
}

ID3D11Texture2D* GameHook::Frame()
{
    if (m_dead) return nullptr;
    // The game resized or reset its swap chain: the hook made a new texture.
    if (Signalled(m_ready) && !Open()) m_dead = true;
    if (Signalled(m_process)) m_dead = true;
    return m_dead ? nullptr : m_texture.get();
}

void GameHook::Close()
{
    if (m_stop) SetEvent(m_stop);
    m_texture = nullptr;
    if (m_data) UnmapViewOfFile(m_data);
    m_data = nullptr;
    if (m_info) UnmapViewOfFile(m_info);
    m_info = nullptr;
    CloseOne(m_dataMap);
    CloseOne(m_infoMap);
    CloseOne(m_restart);
    CloseOne(m_stop);
    CloseOne(m_init);
    CloseOne(m_ready);
    CloseOne(m_exit);
    CloseOne(m_keepalive);
    CloseOne(m_process);
}

GameHook::~GameHook() { Close(); }
