// Exo Capture - shared helpers. GPL-3.0-or-later.
#pragma once

#include <windows.h>
#include <unknwn.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <mftransform.h>
#include <codecapi.h>
#include <strmif.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

using winrt::com_ptr;
namespace json = winrt::Windows::Data::Json;

/// A failed Windows call, with what was being done.
struct Failure : std::exception
{
    HRESULT hr;
    std::string what_;
    Failure(HRESULT code, const char* doing) : hr(code)
    {
        char text[64];
        sprintf_s(text, " (0x%08X)", static_cast<unsigned>(code));
        what_ = std::string(doing) + text;
    }
    const char* what() const noexcept override { return what_.c_str(); }
};

inline void Check(HRESULT hr, const char* doing)
{
    if (FAILED(hr)) throw Failure(hr, doing);
}

/// One line to stderr; Exo writes it to its debug log.
void Log(const std::string& line);
std::string Narrow(const std::wstring& text);
std::wstring Widen(const std::string& text);

/// One event line on stdout: {"event": name, "data": data}.
void Emit(const wchar_t* name, const json::JsonObject& data);
/// A reply to request id: {"id": id, "result": ...} or {"id": id, "error": "..."}.
void Reply(double id, const json::IJsonValue& result);
void ReplyError(double id, const std::string& message);

/// QueryPerformanceCounter in 100 ns units, the clock Windows.Graphics.Capture and WASAPI stamp with.
int64_t Now100ns();

/// "Name 2026-10-17 21.03.05" style local timestamp.
std::wstring Stamp();
/// A file or folder name with characters Windows refuses replaced.
std::wstring SafeName(const std::wstring& name);
/// A path that does not exist yet: base + ext, or base (2) + ext, ...
std::wstring FreePath(const std::wstring& folder, const std::wstring& base, const std::wstring& ext);
bool EnsureFolder(const std::wstring& folder);

json::JsonObject Obj();
std::wstring GetString(const json::JsonObject& o, const wchar_t* key, const std::wstring& fallback = L"");
double GetNumber(const json::JsonObject& o, const wchar_t* key, double fallback);
bool GetBool(const json::JsonObject& o, const wchar_t* key, bool fallback);

/// Saves BGRA pixels as JPEG (thumbnails) or PNG (snips) with WIC.
void SaveImage(const std::wstring& path, const uint8_t* bgra, UINT width, UINT height, UINT stride, bool png, UINT maxWidth = 0);
/// BGRA pixels as JPEG bytes in memory, scaled down to maxWidth when wider (Go Live's picker and preview).
std::vector<uint8_t> EncodeJpeg(const uint8_t* bgra, UINT width, UINT height, UINT stride, UINT maxWidth, float quality = 0.8f);
/// Bytes as base64, for a JSON reply.
std::wstring Base64(const std::vector<uint8_t>& bytes);

/// Reads an image file as BGRA pixels (top-down).
bool ReadImageFile(const std::wstring& path, std::vector<uint8_t>& pixels, UINT& width, UINT& height);

/// Copies BGRA pixels to the clipboard as a bitmap.
bool CopyImageToClipboard(HWND owner, const uint8_t* bgra, UINT width, UINT height, UINT stride);
/// Puts a file on the clipboard as a file drop, so it pastes into Discord or Explorer.
bool CopyFileToClipboard(HWND owner, const std::wstring& path);
