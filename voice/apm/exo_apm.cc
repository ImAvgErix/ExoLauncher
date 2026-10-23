// Exo's echo cancellation and noise suppression from WebRTC's audio processing (webrtc-audio-processing 2.1, BSD-3-Clause),
// the processing Chrome and Discord's voice are built on, behind a C interface Exo calls from C#: AEC3 with the high-pass
// filter it needs (exo_aec_*), and its noise suppression (exo_ns_*). Mono, 48 kHz, 10 ms (480 samples) a call: what Exo plays on the render side, the microphone on the
// capture side, from two threads (APM locks each side itself). AEC3 finds the delay between them on its own.
#include <cstdint>
#include "api/scoped_refptr.h"
#include "modules/audio_processing/include/audio_processing.h"

namespace {
const webrtc::StreamConfig kMono(48000, 1);
}

extern "C" {

__declspec(dllexport) void* exo_aec_create() {
  rtc::scoped_refptr<webrtc::AudioProcessing> apm = webrtc::AudioProcessingBuilder().Create();
  if (!apm) return nullptr;
  webrtc::AudioProcessing::Config config;
  config.echo_canceller.enabled = true;
  config.echo_canceller.mobile_mode = false;
  config.high_pass_filter.enabled = true;
  apm->ApplyConfig(config);
  return apm.release();
}

// What Exo plays, 480 samples; returns 0 or an APM error.
__declspec(dllexport) int exo_aec_render(void* handle, const float* samples) {
  const float* in[1] = {samples};
  return static_cast<webrtc::AudioProcessing*>(handle)->AnalyzeReverseStream(in, kMono);
}

// The microphone, 480 samples, cleaned in place; returns 0 or an APM error.
__declspec(dllexport) int exo_aec_capture(void* handle, float* samples) {
  const float* in[1] = {samples};
  float* out[1] = {samples};
  return static_cast<webrtc::AudioProcessing*>(handle)->ProcessStream(in, kMono, kMono, out);
}

__declspec(dllexport) void exo_aec_destroy(void* handle) {
  static_cast<webrtc::AudioProcessing*>(handle)->Release();
}

// WebRTC's own noise suppression alone (Discord's "Standard"), level 0 to 3 (low, moderate, high, very high). Mono,
// 48 kHz, 10 ms a call, on one thread; it adds no delay.
__declspec(dllexport) void* exo_ns_create(int level) {
  rtc::scoped_refptr<webrtc::AudioProcessing> apm = webrtc::AudioProcessingBuilder().Create();
  if (!apm) return nullptr;
  webrtc::AudioProcessing::Config config;
  config.noise_suppression.enabled = true;
  config.noise_suppression.level = static_cast<webrtc::AudioProcessing::Config::NoiseSuppression::Level>(level < 0 ? 0 : level > 3 ? 3 : level);
  apm->ApplyConfig(config);
  return apm.release();
}

// 480 samples cleaned in place; returns 0 or an APM error.
__declspec(dllexport) int exo_ns_process(void* handle, float* samples) {
  const float* in[1] = {samples};
  float* out[1] = {samples};
  return static_cast<webrtc::AudioProcessing*>(handle)->ProcessStream(in, kMono, kMono, out);
}

__declspec(dllexport) void exo_ns_destroy(void* handle) {
  static_cast<webrtc::AudioProcessing*>(handle)->Release();
}
}
