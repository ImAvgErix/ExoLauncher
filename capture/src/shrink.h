// Exo Capture - shrink: re-encodes a finished recording to a much smaller file at the same look, and proves it did.
// GPL-3.0-or-later.
#pragma once
#include "common.h"
#include "video.h"

/// What a shrunk file must pass before it is reported ok; anything less is deleted and the reason returned.
/// The gates are from measurements on an RTX 3070 (NVENC HEVC), SSIM of 24 evenly spaced frames, old against new:
///   - the encoder at its default quality (70): 0.994 mean / 0.993 worst frame on a synthetic 1080p60 clip, 0.999 / 0.992
///     on a real desktop recording of Exo Capture's own;
///   - a squeeze that visibly softens detail: 2.5 Mbit/s (13% of the file) 0.984 / 0.977, 350 kbit/s (6%) 0.927 / 0.924.
/// So 0.985 mean sits just under what quality 40 (13% of the file) still scores and well above anything visibly worse,
/// and 0.95 for the worst frame (the text and fine-detail areas go first) is wider than the 0.99 an ordinary encode
/// keeps. Ordinary encoder noise passes; a file that lost detail does not.
/// The new file must also be at least 15% smaller, or a second copy is not worth keeping.
constexpr int ShrinkSamples = 24;
constexpr double ShrinkSsimMean = 0.985;
constexpr double ShrinkSsimMin = 0.95;
constexpr double ShrinkMinSaving = 0.15;
constexpr double ShrinkDurationSlack = 0.25; // seconds
constexpr double ShrinkFrameSlack = 0.01;    // of the frame count
constexpr int ShrinkDefaultQuality = 70;

struct ShrinkOptions
{
    std::wstring input;
    /// Empty: "<name>.shrink.mp4" beside the input.
    std::wstring output;
    /// "auto" (AV1, else HEVC, else H.264: the best this GPU encodes and Windows plays back), "av1", "hevc" or "h264".
    /// A codec the GPU lacks steps down; one worse than the recording's own is never used.
    std::wstring codec = L"auto";
    /// 0..100, higher looks closer to the original and saves less.
    int quality = ShrinkDefaultQuality;
    /// 0..100, called at most twice a second.
    std::function<void(double)> progress;
    /// Set to true to stop: the partial file is deleted.
    const std::atomic<bool>* cancel = nullptr;
    /// Self-test only: encode at this bit rate (a deliberately poor file); 0 uses the quality.
    UINT forceBitrate = 0;
    /// Self-test only: skip the constant-quality mode, as an encoder without one would.
    bool forceVbr = false;
    /// Self-test only: decode on the CPU, as a PC without the GPU decoder would.
    bool cpuDecode = false;
};

struct ShrinkResult
{
    bool ok = false;
    /// Why the original was left alone ("not-smaller", "quality-loss", "hdr-unsupported", ...); empty when ok.
    std::string skipped;
    std::string detail;
    std::wstring input, output;
    uint64_t inputBytes = 0, outputBytes = 0;
    double ratio = 0; // outputBytes / inputBytes
    std::wstring codec;
    std::wstring encoder;
    std::string mode; // quality | vbr | cbr
    double seconds = 0;
    double ssimMean = 0, ssimMin = 0;
    uint64_t framesIn = 0, framesOut = 0;
};

/// Writes the shrunk file next to the input (never touching the input), verifies it, and only then gives it its name.
ShrinkResult Shrink(const ShrinkOptions& options);

/// What VerifyShrink found; reason is empty when the new file passes.
struct ShrinkVerdict
{
    std::string reason;
    std::string detail;
    double ssimMean = 0, ssimMin = 0;
    uint64_t framesIn = 0, framesOut = 0;
};
/// Every check but the encoder's own: size, tracks, duration, audio packets, color, every frame decodes, frame count,
/// and the sampled picture quality.
/// minSaving is how much smaller the new file must be (self-test only lowers it, to reach the later checks).
ShrinkVerdict VerifyShrink(GpuDevice& gpu, const std::wstring& input, const std::wstring& output, const std::atomic<bool>* cancel,
    const std::function<void(double)>& progress = {}, double minSaving = ShrinkMinSaving);

/// Structural similarity of two luma planes (w x h, tight), 8x8 windows every 4 pixels; range is the largest sample value.
double Ssim(const uint16_t* a, const uint16_t* b, UINT width, UINT height, UINT range);

json::JsonObject ShrinkJson(const ShrinkResult& result);
