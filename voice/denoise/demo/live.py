"""Live test: your microphone through Exo Denoise to your speakers (or a virtual cable, which makes it a virtual mic).

    python demo/live.py out/exo-denoise.onnx                 # default devices; Enter toggles bypass, Ctrl+C stops
    python demo/live.py out/exo-denoise.onnx --list          # list devices
    python demo/live.py out/exo-denoise.onnx --input 3 --output 7 --atten 70
    python demo/live.py out/exo-denoise.onnx --file noisy.wav --save clean.wav   # same streaming path, no devices

To use it as a microphone in Discord or anything else on Windows, install VB-Audio Virtual Cable, pick "CABLE Input"
as --output here and "CABLE Output" as the app's microphone. Use headphones when listening live: speakers feed back.
Audio runs at 48 kHz in 10 ms blocks; each block is processed in the audio callback on one thread (the model takes
about 1 ms of the 10), so nothing is queued and the added delay is the model's own 10 ms plus the devices'.
"""
from __future__ import annotations

import argparse
import sys
import threading
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from exodenoise.stream import StreamingDenoiser  # noqa: E402


def run_file(den: StreamingDenoiser, src: str, dst: str | None) -> None:
    import soundfile as sf

    from exodenoise import dsp

    x, sr = sf.read(src, dtype="float32", always_2d=True)
    x = dsp.resample(x.mean(1), sr)
    t0 = time.perf_counter()
    y = den.process_file(x)
    took = time.perf_counter() - t0
    print(f"{len(x) / 48000:.1f} s of audio in {took:.2f} s (real-time factor {took / (len(x) / 48000):.3f})")
    if dst:
        sf.write(dst, y, 48000)
        print("wrote", dst)


def run_live(den: StreamingDenoiser, args) -> None:
    import sounddevice as sd

    bypass = threading.Event()
    stats = {"ms": [], "late": 0}

    def callback(indata, outdata, frames, t, status):
        if status:
            stats["late"] += 1
        hop = indata[:, 0].copy()
        t0 = time.perf_counter()
        y = hop if bypass.is_set() else den.process(hop)
        stats["ms"].append((time.perf_counter() - t0) * 1000)
        outdata[:] = y[:, None]

    def keys():
        for _ in sys.stdin:
            if bypass.is_set():
                bypass.clear()
            else:
                bypass.set()
            print("bypass" if bypass.is_set() else "denoise", flush=True)

    threading.Thread(target=keys, daemon=True).start()
    with sd.Stream(samplerate=48000, blocksize=den.hop, channels=1, dtype="float32", latency="low",
                   device=(args.input, args.output), callback=callback) as stream:
        print(f"running: input {stream.latency[0] * 1000:.1f} ms + output {stream.latency[1] * 1000:.1f} ms device latency, "
              f"+ 10 ms model. Enter toggles bypass.")
        try:
            while True:
                time.sleep(2)
                ms = stats["ms"][-200:]
                if ms:
                    print(f"hop {np.mean(ms):.2f} ms (max {np.max(ms):.2f}), voice {den.lsnr:5.1f} dB SNR, "
                          f"xruns {stats['late']}", flush=True)
        except KeyboardInterrupt:
            pass


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("--atten", type=float, default=100.0, help="most the noise is turned down, dB (Exo: 70)")
    ap.add_argument("--input", type=int)
    ap.add_argument("--output", type=int)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--file")
    ap.add_argument("--save")
    a = ap.parse_args()
    if a.list:
        import sounddevice as sd

        print(sd.query_devices())
        return
    den = StreamingDenoiser(a.model, atten_lim_db=a.atten)
    if a.file:
        run_file(den, a.file, a.save)
    else:
        run_live(den, a)


if __name__ == "__main__":
    main()
