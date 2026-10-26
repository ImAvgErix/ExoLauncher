# Exo Capture: instant replay, recording and snips for Exo Launcher.
# GPL-3.0-or-later (see LICENSE). Built by build.ps1 with the MSVC toolset.
cmake_minimum_required(VERSION 3.25)
project(ExoCapture LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_executable(ExoCapture
    src/main.cpp
    src/common.cpp
    src/video.cpp
    src/hook.cpp
    src/audio.cpp
    src/mux.cpp
    src/shrink.cpp
    src/ui.cpp
    src/ExoCapture.manifest)

target_compile_definitions(ExoCapture PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)
target_compile_options(ExoCapture PRIVATE /W3 /EHsc /permissive- /utf-8 /bigobj /await:strict)
target_link_libraries(ExoCapture PRIVATE
    d3d11 dxgi d2d1 dwrite windowscodecs
    mfplat mfuuid mfreadwrite mf strmiids
    mmdevapi ole32 oleaut32 shell32 shlwapi user32 gdi32 dwmapi windowsapp)

# The self-test of Shrink (not part of the shipped program): pwsh -File build.ps1 -Test builds and runs it.
add_executable(ExoCaptureShrinkTest EXCLUDE_FROM_ALL
    src/shrink_selftest.cpp
    src/shrink.cpp
    src/common.cpp
    src/video.cpp
    src/hook.cpp
    src/mux.cpp)
target_compile_definitions(ExoCaptureShrinkTest PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)
target_compile_options(ExoCaptureShrinkTest PRIVATE /W3 /EHsc /permissive- /utf-8 /bigobj /await:strict)
target_link_libraries(ExoCaptureShrinkTest PRIVATE
    d3d11 dxgi d2d1 dwrite windowscodecs
    mfplat mfuuid mfreadwrite mf strmiids
    mmdevapi ole32 oleaut32 shell32 shlwapi user32 gdi32 dwmapi windowsapp)
enable_testing()
add_test(NAME shrink COMMAND ExoCaptureShrinkTest)
