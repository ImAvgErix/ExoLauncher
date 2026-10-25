"""Latency and cost: python -m exodenoise.bench out/exo-denoise.onnx [more.onnx ...] [--threads 1] [--seconds 60]

For each model: per-hop wall time (mean, p50, p99, max) over a stream of noisy speech-like audio, real-time factor
(compute time / audio time) at one thread, the measured in-stream delay (cross-correlation of output with input, with suppression off),
algorithmic latency (window + lookahead), the PyTorch model's parameters and MACs per second of audio.
"""
from __future__ import annotations

import argparse
import json
import platform
import time

import numpy as np
import torch

from . import dsp
from .stream import StreamingDenoiser


def macs_per_second(model) -> float:
    """MACs of one streaming hop, counted from the layers' shapes with forward hooks, times 100 hops a second."""
    from .model import Streaming

    total = 0

    def conv(m, i, o):
        nonlocal total
        k = m.weight[0].numel()  # cin/groups * kt * kf
        total += o.numel() * k

    def linear(m, i, o):
        nonlocal total
        total += o.numel() * (m.weight.shape[1] if isinstance(m, torch.nn.Linear) else m.weight.shape[1])

    def gru(m, i, o):
        nonlocal total
        steps = i[0].shape[0] * i[0].shape[1]
        d = 2 if m.bidirectional else 1
        total += steps * d * 3 * m.hidden_size * (m.input_size + m.hidden_size)

    hooks = []
    for mod in model.modules():
        if isinstance(mod, (torch.nn.Conv2d, torch.nn.ConvTranspose2d)):
            hooks.append(mod.register_forward_hook(conv))
        elif isinstance(mod, torch.nn.Linear) or type(mod).__name__ == "GroupedLinear":
            hooks.append(mod.register_forward_hook(linear))
        elif isinstance(mod, torch.nn.GRU):
            hooks.append(mod.register_forward_hook(gru))
    s = Streaming(model)
    with torch.no_grad():
        s(torch.zeros(1, dsp.HOP), s.initial_state(), torch.tensor([100.0]))
    for h in hooks:
        h.remove()
    stft = 2 * dsp.N_FFT * 2 * dsp.N_FREQS  # analysis and synthesis DFT products
    return (total + stft) * dsp.SR / dsp.HOP


def test_signal(seconds: float, seed: int = 0) -> np.ndarray:
    """Harmonic 'voice' with a moving pitch, syllable envelope and pink-ish noise: enough for timing and delay."""
    rng = np.random.default_rng(seed)
    t = np.arange(int(seconds * dsp.SR)) / dsp.SR
    f0 = 140 + 30 * np.sin(2 * np.pi * 0.7 * t)
    phase = 2 * np.pi * np.cumsum(f0) / dsp.SR
    voice = sum(np.sin(k * phase) / k for k in range(1, 20)) * (0.5 + 0.5 * np.sin(2 * np.pi * 3 * t)).clip(0) ** 2
    noise = np.cumsum(rng.standard_normal(len(t))) * 0.002
    noise -= np.convolve(noise, np.ones(480) / 480, "same")
    return (0.05 * voice + noise + 0.005 * rng.standard_normal(len(t))).astype(np.float32)


def measured_delay(x: np.ndarray, y: np.ndarray, most: int = 4 * dsp.HOP) -> int:
    best, at = -np.inf, 0
    for d in range(most):
        c = float(np.dot(x[: len(x) - d], y[d:]))
        if c > best:
            best, at = c, d
    return at


def bench(path: str, seconds: float, threads: int) -> dict:
    den = StreamingDenoiser(path, threads=threads)
    x = test_signal(seconds)
    n = len(x) // dsp.HOP * dsp.HOP
    for i in range(0, 100 * dsp.HOP, dsp.HOP):  # warm up
        den.process(x[i: i + dsp.HOP])
    den.reset()
    times, out = [], []
    for i in range(0, n, dsp.HOP):
        t0 = time.perf_counter()
        out.append(den.process(x[i: i + dsp.HOP]))
        times.append(time.perf_counter() - t0)
    ms = np.array(times) * 1000
    # The pipeline's own delay, from a pass with no suppression (attenuation limit 0 dB: output = input, delayed).
    passthrough = StreamingDenoiser(path, atten_lim_db=0.0, threads=threads)
    y = np.concatenate([passthrough.process(x[i: i + dsp.HOP]) for i in range(0, n, dsp.HOP)])
    return {
        "model": path, "threads": threads,
        "hop_ms_mean": float(ms.mean()), "hop_ms_p50": float(np.percentile(ms, 50)),
        "hop_ms_p99": float(np.percentile(ms, 99)), "hop_ms_max": float(ms.max()),
        "rtf": float(ms.sum() / 1000 / (n / dsp.SR)),
        "measured_delay_ms": measured_delay(x[:n], y) / dsp.SR * 1000,
        "algorithmic_latency_ms": dsp.N_FFT / dsp.SR * 1000,
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("models", nargs="+")
    ap.add_argument("--threads", type=int, default=1)
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--checkpoint", help="best.pt for the parameter and MAC count (else an untrained model's)")
    ap.add_argument("--json")
    a = ap.parse_args()
    from .export import load
    from .model import ExoDenoise, count_params

    model = load(a.checkpoint) if a.checkpoint else ExoDenoise().eval()
    info = {"cpu": platform.processor() or platform.machine(), "params": count_params(model),
            "gmacs_per_s": macs_per_second(model) / 1e9}
    try:
        info["cpu"] = next(l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name"))
    except (OSError, StopIteration):
        pass
    rows = [bench(m, a.seconds, a.threads) for m in a.models]
    print(json.dumps(info))
    for r in rows:
        print(json.dumps(r))
    if a.json:
        json.dump({"info": info, "rows": rows}, open(a.json, "w"), indent=1)


if __name__ == "__main__":
    main()
