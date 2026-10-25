# Exo Denoise

Real-time full-band speech noise suppression for Exo's Discord voice: a causal 48 kHz model with 20 ms algorithmic
latency (10 ms in-stream delay), 0.58 M parameters and 0.59 GMAC/s (well under 1 ms of one CPU thread per 10 ms
hop), with everything needed to train, test, export and run it: on-the-fly data mixing, a training loop with
validation and tracking, baselines, ONNX export with streaming state, quantization, benchmarks, a C library with the
interface Exo already calls, and a live demo.

How it works and why it is built this way: [docs/design.md](docs/design.md).

```
exodenoise/
  dsp.py        causal STFT (20 ms Vorbis window, 10 ms hop), ERB bands, DFT matrices
  model.py      ExoDenoise (offline, for training) and Streaming (one hop, state in one vector)
  losses.py     multi-resolution compressed spectral, asymmetric over-suppression, SI-SDR, local SNR
  metrics.py    PESQ, STOI/ESTOI, SI-SDR, DNSMOS P.835
  data/         download.py (public corpora), manifest.py (file lists), mixer.py (on-the-fly mixing)
  train.py      training loop: EMA, checkpoints, resume, validation, JSONL + TensorBoard
  export.py     ONNX export, checked against PyTorch; INT8 / FP16 variants
  stream.py     the exported model as a streaming denoiser (numpy + ONNX Runtime only)
  bench.py      per-hop latency, real-time factor, measured delay, parameters, MACs
  baselines.py  DeepFilterNet 3 (offline and low-latency), RNNoise, Demucs, DTLN, any licensed SDK's CLI
  evaluate.py   scores output folders against references, alignment-checked
capi/           exo-denoise.dll: the model behind DeepFilterNet's df_* C interface (NoiseSuppressor.cs)
demo/live.py    microphone → model → speakers or a virtual cable; or a file through the same stream
configs/        exo_s.yaml (full recipe, shipping size), exo_m.yaml (larger), vb_demand.yaml (small recipe)
tests/          pytest: reconstruction, streaming = offline, ONNX = PyTorch, delay, mixing, losses
```

## Setup

Python 3.11-3.13. Install PyTorch for your platform from pytorch.org first (CUDA for training), then:

```sh
cd voice/denoise
python -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt            # + tensorboard, sounddevice if wanted
python -m pytest -q tests                  # 11 tests, about two minutes on a CPU
```

Baselines run in their own Python 3.11 environment, since DeepFilterNet's package needs torchaudio's older API:

```sh
uv venv -p 3.11 .venv-baselines && . .venv-baselines/bin/activate
uv pip install -r requirements-baselines.txt --extra-index-url https://download.pytorch.org/whl/cpu
```

## Data

`prepare.sh` fetches the small recipe (about 8 GB: VoiceBank 28 speakers, DEMAND's 13 training noise types, DNS 5's
RIRs, emotional speech and Freesound noise) into `data/` and writes the manifests `configs/vb_demand.yaml` reads.

The full recipe (`configs/exo_s.yaml`) adds DNS 5's fullband read speech (Microsoft's
`download-dns-challenge-5-headset-training.sh` from github.com/microsoft/DNS-Challenge; 300 GB unpacked for read
speech alone), VCTK, EARS, DNS AudioSet noise and OpenSLR RIRs:

```sh
python -m exodenoise.data.download data dns_vctk dns_audioset openslr_rirs ears
python -m exodenoise.data.manifest data/manifests/speech_dns_read.jsonl <datasets_fullband/clean_fullband/read_speech>
python -m exodenoise.data.manifest data/manifests/speech_vctk.jsonl data/dns_vctk --min-seconds 1
python -m exodenoise.data.manifest data/manifests/speech_ears.jsonl data/ears --min-seconds 1
python -m exodenoise.data.manifest data/manifests/noise_dns.jsonl data/dns_freesound data/dns_audioset --min-seconds 1
python -m exodenoise.data.manifest data/manifests/rir_dns.jsonl data/dns_rir
python -m exodenoise.data.manifest data/manifests/rir_openslr.jsonl data/openslr_rirs --exclude 'noise'
```

A manifest is one JSON line per file (path, rate, frames, channels); any folder of audio at any rate works. Keep the
test set's noise out of training: VoiceBank-DEMAND's test set uses DEMAND's DLIVING, OOFFICE, TBUS, SCAFE and
SPSQUARE, which `download.py` never fetches. Corpus licenses differ (EARS and Demucs's weights are non-commercial);
`download.py` lists them.

## Training

```sh
python -m exodenoise.train configs/exo_s.yaml                       # a run in runs/exo-s
python -m exodenoise.train configs/exo_s.yaml --resume              # continue from last.pt
python -m exodenoise.train configs/vb_demand.yaml --set train.steps=2000 model.channels=48 run=runs/ablation-c48
tensorboard --logdir runs                                            # or read runs/*/metrics.jsonl
```

`--set` overrides any config key, which is how ablations are run. A run directory holds `config.yaml` (as run, with
the git commit), `metrics.jsonl`, `tb/`, `last.pt` (resumable) and `best.pt` (EMA weights with the best validation
PESQ). The full recipe's 300k steps of 32 × 3 s are sized for one modern GPU over a few days (an estimate: this
repository's own run was on a CPU, below). On a CPU, keep `train.threads` at 1-2 per process: small GRUs on many threads of a busy CPU run 30 to 50
times slower, measured.

Real recordings with no clean reference can join training as pseudo-labels: run a teacher over them
(`python -m exodenoise.baselines dfn3 real/ real_dfn3/`) and add to the config's `data`:
`pseudo: {noisy: real/, target: real_dfn3/, every: 4, weight: 0.5}`.

## Evaluation

```sh
python -m exodenoise.export runs/exo-s/best.pt out/                  # out/exo-denoise.onnx
python -m exodenoise.baselines exo:out/exo-denoise.onnx data/vb_test/noisy_testset_wav out/vb_exo
# in the baselines environment:
for b in dfn3 dfn3_ll rnnoise demucs dtln; do python -m exodenoise.baselines $b data/vb_test/noisy_testset_wav out/vb_$b; done
python -m exodenoise.evaluate --clean data/vb_test/clean_testset_wav --dnsmos --markdown results.md \
  --system noisy=data/vb_test/noisy_testset_wav --system exo=out/vb_exo --system dfn3_ll=out/vb_dfn3_ll ...
```

Exo Denoise is scored through its exported ONNX graph streamed hop by hop, exactly as it runs live. Any folder of
outputs can be scored: a licensed SDK through `baselines.py "cmd:<tool> {in} {out}"` (NVIDIA Maxine's sample app with
a denoiser config, a Krisp SDK sample), or recordings made elsewhere. For real noisy recordings with no reference,
leave out `--clean` and DNSMOS alone scores them.

## Results

VoiceBank-DEMAND test set, all 824 files (unseen speakers and unseen noise types; PESQ wide band and STOI at
16 kHz, DNSMOS P.835 overall). Exo Denoise is scored through its exported ONNX graph streamed hop by hop. Every
output was alignment-checked (all within 0.01 ms). Raw numbers: `results/*.json`.

| System | Latency | Params | PESQ | STOI | ESTOI | SI-SDR | DNSMOS SIG | BAK | OVRL |
|---|---|---|---|---|---|---|---|---|---|
| Noisy input | – | – | 1.97 | 0.921 | 0.787 | 8.4 | 3.32 | 3.11 | 2.68 |
| RNNoise | 20 ms | small | 2.11 | 0.889 | 0.783 | 12.3 | 3.27 | 3.82 | 2.91 |
| DTLN (16 kHz) | 32 ms | 1.0 M | 2.39 | 0.919 | 0.821 | 17.6 | 3.29 | 3.81 | 2.93 |
| Demucs dns64 (16 kHz, offline) | – | 33.5 M | 2.56 | 0.932 | 0.854 | 17.6 | 3.37 | 4.03 | 3.10 |
| **Exo Denoise S, this run** | 20 ms | 0.58 M | 2.74 | 0.937 | 0.840 | 14.2 | 3.34 | 3.92 | 3.01 |
| DeepFilterNet 3 low-latency (Exo's current) | 20 ms | 9.7 M | 3.07 | 0.950 | 0.870 | 18.7 | 3.47 | 4.06 | 3.20 |
| DeepFilterNet 3 offline | 40 ms | 2.1 M | 3.16 | 0.945 | 0.865 | 18.9 | 3.44 | 4.08 | 3.18 |

**This run does not beat DeepFilterNet 3, and so does not replace what Exo ships.** It was trained on a 4-core CPU
for 6000 steps of 16 × 2 s mixes (53 hours of audio seen, from 9.5 hours of speech): `configs/vb_demand.yaml`,
`runs/vb-demand-s/metrics.jsonl` locally. DeepFilterNet 3 was trained on hundreds of hours for 120 epochs on GPUs. At
1/17 of its weights, Exo Denoise already leads RNNoise and DTLN on PESQ and STOI and Demucs on PESQ and STOI, but
trails all three model-based baselines on SI-SDR, and DeepFilterNet 3 on everything. Whether the full recipe
(`configs/exo_s.yaml`) closes that gap is the open question; it needs a GPU.

The 70 dB attenuation limit Exo uses changes nothing here (PESQ 2.735 with or without it): the test set's noise never
needs more than 70 dB. INT8 matches FP32 to the third decimal on every metric.

Cost on one thread of an Intel Xeon (cloud vCPU), 60 s streamed (`results/bench.json`):

| Graph | Mean per 10 ms hop | p99 | Real-time factor | Measured delay |
|---|---|---|---|---|
| FP32 | 0.82 ms | 0.96 ms | 0.082 | 10 ms |
| INT8 | 0.81 ms | 1.02 ms | 0.081 | 10 ms |
| FP16 | 0.95 ms | 1.15 ms | 0.095 | 10 ms |

Through the C library (`capi/`, same machine): 0.81 ms a hop, output identical to Python's.

## Export and inference

```sh
python -m exodenoise.export runs/exo-s/best.pt out/ --int8 --fp16
python -m exodenoise.bench out/exo-denoise.onnx out/exo-denoise.int8.onnx --checkpoint runs/exo-s/best.pt
```

The graph takes one 10 ms hop and returns the hop before it, cleaned:

| | name | shape | |
|---|---|---|---|
| in | `hop` | (1, 480) float32 | 48 kHz mono |
| in | `state` | (1, S) float32 | the last call's `new_state`; the first call's is the model's `initial_state` metadata |
| in | `atten_lim_db` | (1,) float32 | the most noise is turned down: 100 for all of it, 70 Exo's default, 0 bypass |
| out | `out` | (1, 480) | the previous hop, cleaned |
| out | `new_state` | (1, S) | |
| out | `lsnr` | (1,) | the model's local SNR for the hop, -15 (noise alone) to 35 dB (clean voice) |

The model's metadata carries `sample_rate`, `hop`, `state_size`, `delay_samples` and `initial_state` (space-separated
floats). In Python:

```python
from exodenoise.stream import StreamingDenoiser
den = StreamingDenoiser("out/exo-denoise.onnx", atten_lim_db=70)
cleaned = den.process(hop)          # 480 float32 samples in, the previous 480 out; den.lsnr for a gate
whole = den.process_file(signal)    # a whole signal, delay removed
```

Live: `python demo/live.py out/exo-denoise.onnx` (Enter toggles bypass). With VB-Audio Virtual Cable as `--output`,
any app can take the cleaned voice as its microphone.

## In Exo

`capi/` builds `exo-denoise.dll` with the model embedded, exporting the functions `NoiseSuppressor.cs` loads from
DeepFilterNet's library today, with the same meaning: `df_create_default(atten_lim)`, `df_set_atten_lim`,
`df_process_frame_checked` (480 samples in and out, the local SNR back, NaN when a hop can't be cleaned, never
throwing), `df_free`, and `df_set_post_filter_beta` and `df_set_thresholds`, accepted and ignored. `df_reset` returns
an instance to its starting state, which can replace the silence `NoiseSuppressor.Flush` pushes through.

```sh
cmake -S capi -B build/capi -DONNXRUNTIME_DIR=<Microsoft.ML.OnnxRuntime package> -DEXO_DENOISE_MODEL=out/exo-denoise.onnx
cmake --build build/capi --config Release        # exo-denoise.dll; ships beside onnxruntime.dll
```

Built and checked on Linux against the Python runtime: identical output, 0.8 ms a hop. Swapping it in for Exo is a
new pinned asset in `NoiseSuppressor.Plugin` (and `onnxruntime.dll` beside it); `DelayTracker` will measure one hop.
That swap, and its check on the installed app with a real call, is not part of this change.
