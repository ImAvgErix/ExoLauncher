// Exo's echo cancellation: WebRTC's AEC3 from webrtc-audio-processing 2.1 (BSD-3-Clause), its echo canceller and the
// high-pass filter it needs and nothing else (Exo has its own noise suppression and gain control), behind a C interface
// Exo calls from C#. Mono, 48 kHz, 10 ms (480 samples) a call: what Exo plays on the render side, the microphone on the
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
}
