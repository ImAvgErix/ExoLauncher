# Exo Setup: Exo's own window for installing, updating and uninstalling Exo Launcher (see src/main.cpp).
# Built by build.ps1 with the MSVC toolset; WebView2's static loader from third_party/wv2 (Microsoft.Web.WebView2 1.0.2903.40).
cmake_minimum_required(VERSION 3.25)
project(ExoSetup LANGUAGES CXX RC)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(EXO_VERSION "0.0.0" CACHE STRING "Exo Launcher version")
string(REPLACE "." "," EXO_VERSION_COMMA "${EXO_VERSION},0")

add_executable(ExoSetup WIN32 src/main.cpp src/ExoSetup.rc)
target_include_directories(ExoSetup PRIVATE third_party/wv2/build/native/include)
target_compile_definitions(ExoSetup PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00 "PRODUCT_VERSION=L\"${EXO_VERSION}\"")
target_compile_options(ExoSetup PRIVATE /W3 /EHsc /permissive- /utf-8 /O2)
set_source_files_properties(src/ExoSetup.rc PROPERTIES COMPILE_DEFINITIONS "VERSION_COMMA=${EXO_VERSION_COMMA};VERSION_TEXT=\"${EXO_VERSION}\"")
target_link_libraries(ExoSetup PRIVATE ${CMAKE_SOURCE_DIR}/third_party/wv2/build/native/x64/WebView2LoaderStatic.lib
    ole32 oleaut32 shell32 shlwapi user32 gdi32 dwmapi version advapi32)
set_target_properties(ExoSetup PROPERTIES LINK_FLAGS "/MANIFEST:NO")
# The page pack is an RCDATA include the resource compiler doesn't track: rebuild the .rc when it changes.
set_source_files_properties(src/ExoSetup.rc PROPERTIES OBJECT_DEPENDS ${CMAKE_SOURCE_DIR}/build/ui.pack)
