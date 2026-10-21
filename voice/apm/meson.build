# exo_apm.dll: WebRTC's AEC3 (webrtc-audio-processing, BSD-3-Clause) for Exo's Discord voice, everything linked in with
# the static C runtime. Built by voice\build.ps1.
project('exo_apm', 'cpp', meson_version: '>= 1.4.0',
  default_options: ['cpp_std=c++17', 'buildtype=release', 'default_library=static', 'b_vscrt=mt'])
apm = dependency('webrtc-audio-processing-2', fallback: ['webrtc-audio-processing', 'audio_processing_dep'])
shared_library('exo_apm', 'exo_apm.cc', dependencies: apm, cpp_args: ['-DWEBRTC_WIN', '-DNOMINMAX'])
