# ExoNS

Causal full-band (48 kHz) speech noise suppression for a voice call. The real-time preset uses a 20 ms analysis window and a 10 ms hop, with no extra lookahead. An impulse comes back one hop later, 10 ms, and nothing of it shows up early. The network adds no further delay. A voice thread that finishes a hop in under 10 ms keeps up with the microphone.

This is the training and export stack. Exo's calls still clean audio with DeepFilterNet 3 (`ExoLauncher/Services/DiscordClient/Voice/NoiseSuppressor.cs`) until a checkpoint from this recipe beats that model on a real set and is built into the same frame contract. `FrameCleaner` is that contract: 20 ms of interleaved stereo, two 10 ms hops, a per-frame local SNR, an attenuation cap, and the same input leveler the voice chain already uses.

## Architecture

The live model is a causal deep filter, not a waveform U-Net.

Speech in a call is an envelope plus a periodic fine structure. DeepFilterNet's split still matches that, and it is the open design that stays real-time at 48 kHz:

1. STFT. Periodic square-root Hann, 960 samples (20 ms), hop 480 (10 ms). The window looks only at samples already in hand. Overlap-add of the same window reconstructs a gain of 1.
2. ERB features. 32 bands, log power, a fixed shift so the feature does not depend on the rest of the clip. Equivalent rectangular bandwidth is the compression GTCRN and DeepFilterNet both use, and it matches loudness better than a linear bin mask.
3. Complex features below 4.8 kHz (96 bins). Magnitude-compressed real and imaginary parts. That is where pitch lives; above it, an ERB gain is enough.
4. Encoder. Frequency-only separable convolutions (time kernel 1, so a frame cannot see its neighbors) and a dual-path GRU on each branch: bidirectional across frequency inside the frame, causal across time at each band. The dual-path block is the piece DPDFNet adds to DeepFilterNet2. Grouped linears join the two branches. Layer norm, not batch norm, so a streamed frame matches the sequence forward.
5. Heads. A local linear on the encoder map plus a GRU on the fused embedding. ERB gains through a sigmoid. Deep-filter coefficients of order 5 (five taps, about 40 ms of past spectrum, which is history, not latency). An identity tap is added on the current frame, and the gain bias starts near 1, so an untrained model passes audio through instead of emitting silence.
6. A local-SNR head, about −15 dB to +35 dB. The same kind of per-frame voice score Exo's gate already reads from DeepFilterNet.

The deep filter is applied to the gain-shaped low bins:

```
Y(t, f) = sum over k of C(t, k, f) * Xg(t - k + L, f)
```

`L` is the lookahead in frames. The real-time preset uses `L = 0`. The quality preset uses `L = 1` (one extra hop, 30 ms) and a second dual-path block.

Suppression depth is not baked into the weights. `atten_lim_db` mixes the enhanced spectrum back toward the noisy one. 0 dB is the input. 70 dB is the default cap Exo uses for a normal room. Skipping the deep filter and keeping only the ERB gains is the light path for a CPU that cannot finish the hop.

Presets (`exons.config`):

| Preset | Lookahead | Measured delay | What changed |
| --- | --- | --- | --- |
| `realtime` | 0 | 10 ms | The voice-chat model. 1.0M parameters |
| `quality` | 1 frame | 20 ms | Wider encoder, one frame of future spectrum. 2.0M parameters |
| `tiny` | 0 | 10 ms | Same STFT, a small net for tests |

## What each tool is doing here

| Tool | Role |
| --- | --- |
| DeepFilterNet 3 | The enhancement framework: ERB gain, complex deep filter, local SNR, 48 kHz, 10 ms hop. Exo already ships v0.5.6 in calls. This repo does not vendor those weights; it trains a model in the same family with a stronger encoder. |
| DPDFNet | Dual-path blocks in the encoder, and the over-attenuation loss so the model stops eating the talker. |
| GTCRN | ERB banding and grouped projections, which keep the full-band net small enough for one core. |
| RNNoise | The idea of a per-frame voice score beside the suppressor, which the gate uses. A Bark-gain plus pitch filter is not the live net: it does not hold up on non-stationary noise the way a complex filter does. |
| DTLN | Instant layer norm (we use layer norm for the same reason: streamed frames match training). The 16 kHz learned basis is the wrong front end for a 48 kHz call. |
| Facebook Denoiser / Demucs | Offline teacher only. The published model looks ahead and is heavier than a hop budget. `baselines.try_denoiser` runs it when the package is installed. |
| NVIDIA Maxine | Not linked and not reverse engineered. The usable ideas are public and already in the product: one strength control (the attenuation cap) and taking late room energy down while keeping the direct sound. The training target is the first 40 ms of the impulse response. |
| Krisp | Not extracted from Discord. Krisp's weights are theirs. What is public, and what Exo already measured on the voice path, is the integration: level the microphone toward −24 dBFS (lift at most 40 dB, cut at most 20 dB, and only while the previous frame was voice), read a per-frame voice score, and drop to a lighter stage when the hop runs long. `level.py` and `FrameCleaner` implement that. A licensed SDK can still teach this model: clean a set of files with it and point training at those pairs. |

Closed weights are not in this tree. Copying them out of Discord or out of an SDK redistributes someone else's model.

## Data

A mixture is clean speech, convolved with a room, plus noise at a chosen SNR.

- Speech: DNS Challenge full-band read speech, LibriSpeech / LibriVox, EARS when the fine-tune is about expressive speech. EARS is the set that carries emotion; DNS alone is read speech and will sand it off if it is the only target.
- Noise: DNS noise, DEMAND, MUSAN, FSD50K. The synthetic fallback (no files) builds white, pink and brown noise, a fan, keyboard clicks, and babble from the same voiced synthesizer, so the loop runs without a download.
- Rooms: DNS real and synthetic RIRs, or a synthetic exponential decay. With probability 0.5 the target is the impulse's first 40 ms and the mixture hears the whole tail.
- SNR: uniform from −5 dB to 20 dB. 10% of examples are clean, so a quiet room is left alone. 5% are noise only, so the model is allowed to go to silence when nobody is talking.
- Gain: ± about 12 dB, then a peak limit. The runtime leveler is not a substitute for seeing more than one level in training.
- Bandwidth: a random tilt and a low-pass, so a laptop microphone and a full-band one share a model.

```bash
python -m exons.prepare --speech data/speech --noise data/noise --rir data/rir --out manifest.json
```

DNS-Challenge (https://github.com/microsoft/DNS-Challenge) is the training distribution that matters. VoiceBank-DEMAND is a useful small check and a poor training set: short clips, high SNR, English only. Do not pick a checkpoint from VoiceBank alone.

Distillation, when you have a teacher (a licensed Maxine or Krisp pass, or DeepFilterNet, or Demucs offline): write `clip.noisy.wav` and `clip.clean.wav`. The clean file is the teacher output. Load those as speech/noise is the wrong shape; use them as the `clean` target and the `noisy` input by building a manifest of already-mixed pairs and setting the clean probability to 1 in a one-off config, or extend `MixDataset` with `baselines.load_teacher_pairs`. The loss does not care who produced the target waveform.

## Loss

Multi-resolution compressed spectral loss at 5, 10, 20 and 40 ms (magnitude and complex, compression 0.3), plus the same error again on bins where the estimate is quieter than the target (over-attenuation), plus a small negative SI-SDR, plus L1 on the local SNR against the frame energy of the speech component over the noise component.

SI-SDR is in the log as `sisdr_db` (higher is better). The `sisdr` field is the loss term (lower is better).

## Train

From `voice/ns`, with Python 3.10+:

```bash
pip install -e .
# optional scores and a live mic
pip install -e ".[metrics,mic,data]"
python -m exons.train --preset realtime --manifest manifest.json --out runs/realtime --steps 200000 --batch 8 --segment 4
```

CPU smoke, synthetic mixtures, no manifest:

```bash
python -m exons.train --preset tiny --steps 20 --batch 2 --segment 0.5 --overfit --out runs/smoke --device cpu
```

A run writes `config.json`, `metrics.jsonl`, `last.pt` (weights and optimizer) and `best.pt` (highest validation SI-SDR improvement). Resume with `--resume runs/realtime/last.pt`.

Validation is held-out mixtures from the same generator, scored with SI-SDR and SI-SDR improvement against the noisy input. STOI and PESQ run in `exons.metrics` when those packages are installed. DNSMOS is Microsoft's P.835 ONNX (`sig_bak_ovr.onnx` from the DNS Challenge); construct `exons.metrics.DNSMOS(path)` for a research eval. The file is not shipped: the challenge license is non-commercial research, and Exo is a product.

Suggested full run, once the DNS set is on disk:

1. Train `realtime` on DNS full-band speech and noise plus MUSAN/FSD50K, RIRs on half the batches, 200k steps, batch 8, 4 s crops, AdamW at 1e-3 cosine down to 1e-5.
2. Fine-tune at 1e-4 on EARS (or any expressive studio speech) mixed with the same noises, so the model stops flattening pitch and effort.
3. If a teacher exists, a short distillation pass on teacher waveforms with the same multi-resolution loss.
4. Export, quantize, and reject the checkpoint if the hop time on the target CPU is over 10 ms or the attenuation-cap listening test sounds gated.

This machine has no GPU and no DNS corpus. A smoke run here only proves the loop, the loss, and that a repeated batch gets easier. It does not prove a win over DeepFilterNet 3. That comparison is `baselines.try_deepfilter` on the same clips, after a real training run.

## Export, quantization, streaming

```bash
python -m exons.export --checkpoint runs/realtime/best.pt --out exons.onnx --quantize
python -m exons.bench --preset realtime --seconds 3 --threads 1
python -m exons.demo --synthetic --checkpoint runs/realtime/best.pt --out clean.wav
python -m exons.demo --wav noisy.wav --out clean.wav --checkpoint runs/realtime/best.pt
```

ONNX is the per-frame network only (ERB features and compressed bins in, gains, coefficients, SNR, and the GRU states out). The STFT and the deep filter stay in `StreamProcessor`, which is where the delay is visible. Batch size in the file is 1: one talker, one instance, which is how Exo already runs DeepFilterNet. Dynamic int8 quantization rewrites the MatMuls; GRU state stays in the graph. FP16 and TensorRT are a runtime choice on top of the same file (`trtexec --onnx=exons.onnx --fp16 --saveEngine=exons.plan`) for a GPU that is already awake. A voice call should not spin a GPU up for this.

`FrameCleaner.process_inplace` takes 1920 float32 samples, interleaved stereo, 20 ms at 48 kHz, writes the cleaned mono to both channels, and returns the louder hop's SNR in dB. `limit_db` is the attenuation cap. That is the seam for a later native port: the same hop the C API in `voice/deepfilter/exo-capi.rs` already exposes (`df_process_frame` in, one hop of audio and a local SNR out).

`--mic` needs `sounddevice` and a real device. A file or `--synthetic` is the demo everywhere else.

## Latency

The analysis window is 20 ms of audio already captured. Overlap-add delays an impulse by one hop, 10 ms, and reproduces it as an impulse (the STFT round-trip error on a sine is under 10⁻⁴). Lookahead adds one hop per frame. Compute sits inside the hop: if the hop finishes in under 10 ms, it does not add another block of delay. A 20 ms Exo frame is two hops.

If a hop takes longer than 10 ms, the capture thread falls behind. The light path (`deep_filter=False` on `StreamProcessor`) keeps the ERB gains and drops the complex filter. Exo's existing overload rule, which swaps DeepFilterNet to its light stage after a run of slow frames, is the same idea and stays in the C# voice chain.

## Tests

```bash
cd voice/ns
python -m unittest discover -s tests -v
```

The tests cover reconstruction of the STFT, the leveler constants, streaming against the sequence forward, causality (a later hop does not change audio already produced), the attenuation cap, ONNX parity, int8 export, hop time under the hop for the real-time preset, a finite training step, and a repeated batch whose loss falls.

## Measured

On a 4-vCPU Intel Xeon, one thread, no GPU. `python -m unittest discover -s tests -v` in `voice/ns`: 19 tests, all passing (STFT reconstruction, streaming against the sequence forward, causality, attenuation cap, ONNX parity, int8 export, hop time, a finite step, a repeated batch whose loss falls).

Streaming, PyTorch eager, one thread:

| Preset | Parameters | Delay | ms / 10 ms hop | RTF |
| --- | --- | --- | --- | --- |
| realtime | 1,035,138 | 10 ms | 3.6 | 0.36 |
| quality | 1,999,554 | 20 ms | 6.3 | 0.63 |
| tiny | 10,098 | 10 ms | 1.2 | 0.12 |

The real-time checkpoint exported to ONNX (4.2 MB) runs in ONNX Runtime at 0.95 ms/hop, RTF 0.09. Dynamic int8 (3.7 MB) was 1.09 ms/hop on this CPU: the GRU stays in the graph and the quantizer did not make the hop faster. FP16 or TensorRT was not timed; there is no GPU here.

Training smoke, synthetic mixtures only (no DNS, no real speech), 157 s:

```bash
python -m exons.train --preset realtime --steps 400 --batch 4 --segment 1.0 --out runs/smoke-realtime --device cpu --val-every 50 --seed 0
```

On that run's 8 fixed validation clips, SI-SDR improvement went from 2.0 dB at step 50 to 5.4 dB at step 350 (12.1 dB enhanced, 6.7 dB noisy). On 12 fresh one-second clips the offline model scored 6.9 dB SI-SDR against 4.0 dB noisy and 2.9 dB for the causal spectral gate in `baselines.py`. The live stream matches that once the 10 ms delay is taken out of the score; scoring the stream against an unshifted target makes a working model look ruined, and `exons.demo` compensates before it prints.

DeepFilterNet, Facebook Denoiser and RNNoise were not installed, so they were not scored. Krisp and Maxine were not run. A 400-step synthetic run is not a claim over DeepFilterNet 3. That comparison waits on a DNS full-band training run and `baselines.try_deepfilter` on the same clips.
