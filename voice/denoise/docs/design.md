# Exo Denoise: design

Exo's Discord voice cleans the microphone (and, with Noise suppression for others, each voice heard) with
DeepFilterNet 3's low-latency model through a C library of Exo's (`voice/deepfilter`, `NoiseSuppressor.cs`). Exo
Denoise is the model meant to replace it: same place in the chain, same C interface, same 10 ms hops at 48 kHz, the
same local SNR for the gate, but a smaller network trained with a recipe aimed at what a voice chat needs.

## Constraints

| | Target | Exo Denoise S |
|---|---|---|
| Sample rate | 48 kHz full band | 48 kHz, 481 bins to 24 kHz |
| Algorithmic latency | ≤ 20-30 ms | 20 ms (20 ms window, 10 ms hop, no lookahead) |
| In-stream delay | | 10 ms (one hop), measured |
| Cost | a few % of one core | 0.59 GMAC/s; 0.7-0.8 ms per 10 ms hop on one thread |
| Runtime | ONNX Runtime CPU, optional GPU | one ONNX graph per hop, state in one float vector |

Latency accounting follows the DNS Challenge: window plus lookahead. A 10 ms hop arrives, the 20 ms window ending
with it is analysed, and the previous hop comes out finished, so the output trails the input by 10 ms and the
algorithmic latency, counting the hop's own buffering, is 20 ms. `bench.py` measures the delay from the sound
(cross-correlation with suppression off), and `tests/test_dsp_model.py` holds it at one hop.

## Architecture

```
noisy hop ─ STFT (Vorbis window, DFT as MatMul) ─┬─ 32 ERB band powers, dB ─ running mean norm (1 s) ─ ERB encoder ─┐
                                                 └─ 96 complex bins (0-4.8 kHz) ─ running unit norm ─ DF encoder ───┤
                                                                                       dual-path core ×2 ◀───────────┘
                                         ┌──────────────────────────────────────────────┤
                       ERB decoder (U-Net skips) → 32 band gains      DF decoder → 5-tap complex filter per bin
                                         │                                              │      local SNR head
              spectrum × gains (all 481 bins) ──────────▶ low 96 bins: deep filter over the last 5 frames
                                                                     │
                                       attenuation limit (mix back noisy) ─ inverse DFT ─ overlap-add ─ hop out
```

**Two stages, as DeepFilterNet.** Band gains over 32 ERB bands take the noise floor down everywhere for almost
nothing (RNNoise's idea). A gain per band can't rebuild a voice between its harmonics when noise sits there, so the
low 96 bins, where voiced energy lives, get a 5-tap complex filter over the current and four past frames (deep
filtering), which can reconstruct the periodic structure. The deep filter runs on the gain-masked spectrum, so it
refines stage one rather than redoing it.

**A dual-path core instead of flat GRUs.** DeepFilterNet 3 flattens the encoder output into a 512-wide vector and
runs grouped GRUs (3 layers of 512 per decoder in the low-latency model Exo ships). Exo Denoise keeps the encoder's
8 frequency groups and alternates a bidirectional GRU across the groups within each frame (a frame is whole, so
looking both ways across frequency costs no latency) with a GRU along time for each group, weights shared across
groups (DPCRN, GTCRN). Harmonic structure is modelled across frequency explicitly, temporal context per region, at a
fraction of the weights: 0.58 M parameters, against 2.1 M for DeepFilterNet 3's offline model and about 9.7 M
(counted from its ONNX graphs) for the low-latency model Exo ships.

**DeepFilterNet's features, kept exactly.** The ERB bands (libdf's widths), the 1 s running normalisations and their
starting values are DeepFilterNet's, so what Exo built around the incumbent carries over: the attenuation limit Voice
Tune sets (70, at most 80 dB), the local SNR the gate reads as Discord reads Krisp's, the input level brought to
-24 dBFS before the model.

**Nothing looks ahead.** Every time-axis convolution is causal (two past frames for the first layers, one frame after),
every time GRU is unidirectional, the deep filter's taps are past frames. One hop of lookahead (as DeepFilterNet 3's
offline model uses two) buys measurable quality at 10 ms more latency; Exo's budget keeps it at zero.

**Everything in one graph.** The DFT and its inverse are matrix products with the window folded in, so the exported
graph has no FFT operator, runs on any ONNX Runtime build, and the runtime only carries a float vector from call to
call: STFT input hop, overlap-add tail, running norms, the convolutions' past frames, each GRU's state, the deep
filter's past frames (4,352 floats for S). The attenuation limit is a graph input, so Voice Tune's strength needs no
rebuild.

## Training recipe

On-the-fly mixing (`data/mixer.py`): each example is a fresh draw, so the model never sees the same mix twice.

- **Speech**: DNS 5 fullband read speech (LibriVox), VCTK, EARS (expressive: emotions, whisper, shouting, laughter),
  DNS emotional speech, all at 48 kHz. Lower-rate corpora are brought to 48 kHz and stay band-limited in the target,
  and 10% of full-band clips are brought down to 16/24/32 kHz and back, so the model learns to keep what is there
  and never to invent highs.
- **Noise**: DNS 5 Freesound and AudioSet noise (keyboards, mice, fans, appliances, traffic, crowds), DEMAND,
  one to three at once, each with its own EQ, sometimes in its own room. Another person talking 5-20 dB under the
  speaker at 15-20%: background speech is noise in a voice chat.
- **Rooms**: DNS 5's measured and simulated RIRs (SLR26, SLR28) at 48 kHz. The target keeps the direct sound and
  the first 50 ms: late reverb is removed, the voice's own room color stays (taking all of it off sounds unnatural).
- **Mic and level**: random peaking and shelving EQ on the speech (the target too: a mic's color is the speaker's),
  levels from -45 to -10 dBFS, rare clipping of the input alone, SNR from -5 to 35 dB, noise alone 5% (target
  silence), clean speech alone 5% (target itself: the model must never harm a clean mic).

Losses (`losses.py`):

- compressed spectral loss (magnitude and complex, power 0.3) over four resolutions, 5 to 40 ms windows, on the
  waveform the model actually outputs, so STFT inconsistency is paid for (DeepFilterNet 3's multi-resolution loss);
- an asymmetric term, the same compressed magnitudes only where the output falls below the clean speech:
  over-suppression costs extra. Lost consonants, breaths and a voice's dynamics are what make denoised speech sound
  flat and emotionless; leftover noise at -60 dB nobody hears;
- SI-SDR at a small weight, keeping phase honest;
- local SNR, the frame head against the true SNR, for the gate.

AdamW, warmup then cosine decay, gradient clipping at 1, an EMA of the weights for validation, checkpoints and
export. Validation every N steps: fixed mixes (loss terms, SI-SDR) and a real test set (PESQ, STOI, SI-SDR,
DNSMOS); `best.pt` keeps the EMA weights that scored best on PESQ.

**Pseudo-labels for real recordings.** Synthetic mixes never cover every real mic. `data.pseudo` mixes in crops of
real noisy recordings whose target is a teacher's output (every N steps, at a weight). The teacher is DeepFilterNet 3's
offline model by default (`baselines.py dfn3`): MIT/Apache-2.0, 40 ms of lookahead a real-time student doesn't have,
so the student learns what it could hear with a little more context. Pseudo-labels cap a student near its teacher,
so they are a regulariser on real audio, never the main target.

## How each tool is used

| Tool | Role here | Why |
|---|---|---|
| **DeepFilterNet 3** (MIT/Apache-2.0) | The base design (ERB gains + deep filtering, features, norms, attenuation limit, local SNR); the baseline to beat, both the offline model (40 ms) and the low-latency model Exo ships (20 ms, run from its ONNX export with DeepFilterNet's own feature code); the default pseudo-label teacher | The strongest open real-time full-band model, and the one Exo's voice chain is tuned around |
| **RNNoise** (BSD) | Baseline; its band-gain stage is stage one here | Tiny and 48 kHz, but 22 band gains plus a pitch comb can't remove noise between harmonics: a quality ceiling deep filtering lifts |
| **Facebook Denoiser** (Demucs dns64, CC BY-NC) | Baseline, offline | Waveform U-Net with LSTM, 16 kHz, 33 M parameters: strong on SI-SDR, far too heavy and band-limited for full-band real time |
| **DTLN** (MIT) | Baseline, streamed block by block | Two-stage (magnitude mask, then learned basis), 16 kHz, 32 ms window: the learned-basis idea is good, the band limit and window are not what Exo needs |
| **NVIDIA Maxine** Audio Effects SDK | Optional baseline through `baselines.py cmd:` on a PC with the SDK | Closed, RTX-only, and an 800 MB runtime Exo fetched once and has since removed. Exo can't ship it; its outputs aren't used as training targets here (check the SDK's license before doing so) |
| **Krisp** (in Discord) | A listening and DNSMOS reference: record Discord's own mic test with Krisp on, score the folder with `evaluate` (no clean reference needed) | Closed and licensed to Discord. Its model is not extracted from the Discord app and not distilled: that would break Krisp's and Discord's terms, and Exo never patches the Discord app (AGENTS.md). Exo already matches the parts of Krisp's behaviour that are observable from outside: the -24 dBFS model level, the gate on the model's SNR, the lighter mode under load |
| **DNSMOS P.835** | Validation and model selection on real recordings | The non-intrusive MOS predictor the DNS Challenge ranked by; needs no clean reference |
| **PESQ, STOI/ESTOI, SI-SDR** | Validation, test | The standard intrusive metrics; PESQ wide band and STOI at 16 kHz, so a 16 kHz baseline isn't penalised for its band limit there (DNSMOS is 16 kHz too: full-band quality above 8 kHz needs listening) |

## What was considered and left out

- **Lookahead.** One hop (10 ms) is the cheapest quality gain there is; Exo's chain already spends its budget
  elsewhere (Opus framing, the jitter buffer, echo cancellation). The code is causal by construction; adding
  lookahead means delaying the time-axis padding and the deep filter's taps by the same hops and the output with them.
- **Transformers, Mamba and other offline state-of-the-art** (MP-SENet, SEMamba, CMGAN): strong on VoiceBank-DEMAND
  but non-causal, 16 kHz, or orders of magnitude more compute than a voice thread can spend.
- **GAN / MetricGAN discriminators.** They raise PESQ, but trade it for artifacts and unstable training; the
  asymmetric loss gets at the same perceptual complaint (over-suppression) directly.
- **INT8.** Dynamic INT8 quantization (`export.py --int8`) is supported, but on a model this size the time is in the
  graph's ~200 small operators, not in arithmetic: it measured 0.81 against FP32's 0.82 ms a hop on a Xeon, with every
  test-set metric equal to the third decimal; the GRUs stay FP32 in ONNX Runtime's dynamic quantizer. FP16
  (`--fp16`) is for GPU execution providers; on CPU it is slower (0.95 ms). The shipping graph is FP32.
- **Model swap under load.** Exo's `Overload` runs DeepFilterNet light (no deep filtering stage) when hops run slow.
  At under 1 ms a hop there's no lighter mode to fall back to; `df_set_thresholds` is accepted and ignored.

## Quality vs latency

| Choice | Latency | Effect |
|---|---|---|
| 20 ms window, 10 ms hop (here) | 20 ms | 50 Hz bins: harmonics of a low voice blur together; deep filtering over 5 frames recovers them |
| 40 ms window | 40 ms | finer bins, better on low voices; over budget |
| + 1 hop lookahead | +10 ms | the best quality per ms; left for a "quality" setting |
| 16 kHz (Demucs, DTLN) | – | half the compute, and voices lose everything above 8 kHz (sibilance, air) |
| S (0.58 M, 0.59 GMAC/s) vs M (1.8 M, 1.5 GMAC/s) | same | M has three wider dual-path blocks for PCs with headroom, same graph interface |
