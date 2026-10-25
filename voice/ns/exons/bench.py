"""Latency and real-time factor.

Algorithmic latency is the shift an impulse picks up going through the
streaming processor: the 20 ms analysis window, plus one hop per frame of
lookahead. Real-time factor is wall time divided by audio time, one ORT/PyTorch
thread, which is the budget on a voice thread that also has to encode Opus.
"""

from __future__ import annotations

import argparse
import json
import time

import numpy as np
import torch

from exons.config import load_preset
from exons.model import ExoNS, count_parameters
from exons.streaming import StreamProcessor
from exons.stft import StreamSTFT


def impulse_delay(output: np.ndarray, source: np.ndarray) -> int:
    """Samples by which `output` lags `source`. Positive means the output is late."""
    corr = np.correlate(output, source, mode="full")
    return int(np.argmax(corr) - (len(source) - 1))


def stft_delay_samples(config) -> int:
    n = config.sr
    at = config.sr // 5
    wave = torch.zeros(1, n)
    wave[0, at] = 1.0
    stream = StreamSTFT(config.n_fft, config.hop, torch.hann_window(config.n_fft, periodic=True).sqrt())
    pieces = []
    for index in range(n // config.hop):
        hop = wave[:, index * config.hop : (index + 1) * config.hop]
        pieces.append(stream.synthesize(stream.analyze(hop)))
    out = torch.cat(pieces, dim=-1)[0].numpy()
    return impulse_delay(out, wave[0].numpy())


def model_delay_samples(model: ExoNS) -> int:
    n = model.cfg.sr
    at = model.cfg.sr // 5
    wave = torch.zeros(1, n)
    wave[0, at] = 1.0
    processor = StreamProcessor(model, atten_lim_db=None)
    out, _snr = processor.process_wave(wave)
    return impulse_delay(out[0].cpu().numpy(), wave[0].numpy())


@torch.inference_mode()
def benchmark(model: ExoNS, seconds: float = 3.0, threads: int = 1) -> dict:
    torch.set_num_threads(threads)
    model.eval()
    hop = model.cfg.hop
    hops = int(seconds * model.cfg.sr / hop)
    processor = StreamProcessor(model, atten_lim_db=70.0)
    frame = torch.zeros(hop)
    for _ in range(8):
        processor.process_hop(frame)
    started = time.perf_counter()
    for _ in range(hops):
        processor.process_hop(frame)
    elapsed = time.perf_counter() - started
    audio = hops * hop / model.cfg.sr
    delay = model_delay_samples(model)
    return {
        "parameters": count_parameters(model),
        "threads": threads,
        "seconds": audio,
        "elapsed": elapsed,
        "rtf": elapsed / audio,
        "ms_per_hop": elapsed / hops * 1000.0,
        "hop_ms": hop / model.cfg.sr * 1000.0,
        "latency_ms": delay / model.cfg.sr * 1000.0,
        "stft_latency_ms": stft_delay_samples(model.cfg) / model.cfg.sr * 1000.0,
        "lookahead_frames": model.cfg.lookahead,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Benchmark streaming ExoNS")
    parser.add_argument("--preset", default="realtime", choices=["realtime", "quality", "tiny"])
    parser.add_argument("--seconds", type=float, default=3.0)
    parser.add_argument("--threads", type=int, default=1)
    args = parser.parse_args()
    print(json.dumps(benchmark(ExoNS(load_preset(args.preset)), args.seconds, args.threads), indent=2))


if __name__ == "__main__":
    main()
