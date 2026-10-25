// Exo Denoise behind the C interface Exo's NoiseSuppressor.cs already calls for DeepFilterNet (voice/deepfilter/
// exo-capi.rs): the same six entry points, the same 10 ms hops at 48 kHz, the same local SNR back from each hop.
// The model is exo-denoise.onnx, embedded at build time (CMakeLists.txt), so the library is one file beside
// onnxruntime.dll. A hop's output is the hop before it, cleaned: 10 ms in-stream delay, as DelayTracker measures.
// Nothing here throws into the caller: a hop that fails returns NaN and leaves the output untouched.
#include <onnxruntime_cxx_api.h>

#include <cmath>
#include <cstring>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

#include "model_data.h"  // exo_denoise_model[], exo_denoise_model_size

#if defined(_WIN32)
#define EXO_EXPORT extern "C" __declspec(dllexport)
#else
#define EXO_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

Ort::Env& Environment() {
  static Ort::Env env(ORT_LOGGING_LEVEL_ERROR, "exo-denoise");
  return env;
}

}  // namespace

struct DFState {
  std::unique_ptr<Ort::Session> session;
  std::vector<float> state, initial;
  int64_t hop = 480;
  float atten_lim = 100.f;
};

EXO_EXPORT DFState* df_create_default(float atten_lim) {
  try {
    auto st = std::make_unique<DFState>();
    Ort::SessionOptions opts;
    opts.SetIntraOpNumThreads(1);
    opts.SetInterOpNumThreads(1);
    opts.SetExecutionMode(ExecutionMode::ORT_SEQUENTIAL);
    opts.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
    opts.AddConfigEntry("session.intra_op.allow_spinning", "0");  // a voice thread must not spin between hops
    st->session = std::make_unique<Ort::Session>(Environment(), exo_denoise_model, exo_denoise_model_size, opts);
    Ort::AllocatorWithDefaultOptions alloc;
    auto meta = st->session->GetModelMetadata();
    st->hop = std::stoll(meta.LookupCustomMetadataMapAllocated("hop", alloc).get());
    std::istringstream values(meta.LookupCustomMetadataMapAllocated("initial_state", alloc).get());
    for (float v; values >> v;) st->initial.push_back(v);
    st->state = st->initial;
    st->atten_lim = atten_lim;
    return st.release();
  } catch (...) {
    return nullptr;
  }
}

EXO_EXPORT void df_free(DFState* st) { delete st; }

EXO_EXPORT void df_set_atten_lim(DFState* st, float lim_db) {
  if (st) st->atten_lim = lim_db;
}

// DeepFilterNet's post filter and stage-skipping thresholds have no counterpart here (the model is cheap enough to run
// every stage on every hop); kept so the library loads where DeepFilterNet's did.
EXO_EXPORT void df_set_post_filter_beta(DFState*, float) {}
EXO_EXPORT void df_set_thresholds(DFState*, float, float, float) {}

// Back to the start, as a new model would be: Exo calls this between users of one instance.
EXO_EXPORT void df_reset(DFState* st) {
  if (st) st->state = st->initial;
}

EXO_EXPORT float df_process_frame_checked(DFState* st, float* input, float* output) {
  if (!st || !input || !output) return NAN;
  try {
    auto mem = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
    const int64_t hop_shape[2] = {1, st->hop};
    const int64_t state_shape[2] = {1, static_cast<int64_t>(st->state.size())};
    const int64_t lim_shape[1] = {1};
    Ort::Value inputs[3] = {
        Ort::Value::CreateTensor<float>(mem, input, st->hop, hop_shape, 2),
        Ort::Value::CreateTensor<float>(mem, st->state.data(), st->state.size(), state_shape, 2),
        Ort::Value::CreateTensor<float>(mem, &st->atten_lim, 1, lim_shape, 1),
    };
    static const char* in_names[] = {"hop", "state", "atten_lim_db"};
    static const char* out_names[] = {"out", "new_state", "lsnr"};
    auto outs = st->session->Run(Ort::RunOptions{nullptr}, in_names, inputs, 3, out_names, 3);
    const float* y = outs[0].GetTensorData<float>();
    const float* s = outs[1].GetTensorData<float>();
    for (int64_t i = 0; i < st->hop; i++)
      if (!std::isfinite(y[i])) return NAN;
    std::memcpy(output, y, st->hop * sizeof(float));
    std::memcpy(st->state.data(), s, st->state.size() * sizeof(float));
    return outs[2].GetTensorData<float>()[0];
  } catch (...) {
    return NAN;
  }
}
