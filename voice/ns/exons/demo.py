"""File and microphone demo.

``--synthetic`` builds a noisy clip, cleans it, and prints SI-SDR against the
clean target and against the causal spectral gate. ``--wav`` cleans a file.
``--mic`` needs the optional ``sounddevice`` extra and a machine with a
microphone; this environment usually has neither, and the file path is the one
the tests use.
"""

from __future__ import annotations

import argparse
import wave
from pathlib import Path

import numpy as np
import torch

from exons.baselines import spectral_gate
from exons.config import load_preset
from exons.data import make_example, read_audio
from exons.metrics import improvement
from exons.model import ExoNS
from exons.streaming import FrameCleaner, StreamProcessor
from exons.train import load_model


def write_wav(path: Path, audio: np.ndarray, sr: int) -> None:
    pcm = np.clip(np.asarray(audio, dtype=np.float32), -1.0, 1.0)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sr)
        handle.writeframes((pcm * 32767.0).astype(np.int16).tobytes())


def clean_wave(model: ExoNS, audio: np.ndarray, limit_db: float) -> np.ndarray:
    model.eval()
    processor = StreamProcessor(model, atten_lim_db=limit_db)
    hop = model.cfg.hop
    padded = int(np.ceil(len(audio) / hop) * hop)
    buf = np.zeros(padded, dtype=np.float32)
    buf[: len(audio)] = audio
    out, _snr = processor.process_wave(torch.from_numpy(buf))
    return out[: len(audio)].cpu().numpy()


def synthetic(model: ExoNS, seconds: float, seed: int, limit_db: float) -> dict:
    rng = np.random.default_rng(seed)
    # Force a noisy mix: clean and noise-only draws would make SI-SDR meaningless.
    config = model.cfg
    example = None
    for draw in range(32):
        example = make_example(np.random.default_rng(seed + draw), int(seconds * config.sr), config.sr, config)
        if float(np.mean(np.square(example["noise"]))) > 1e-8 and float(np.mean(np.square(example["clean"]))) > 1e-8:
            break
    assert example is not None
    enhanced = clean_wave(model, example["noisy"], limit_db)
    gated = spectral_gate(example["noisy"], config.sr)
    # The live stream is one hop late (plus any lookahead). SI-SDR against the
    # unshifted target treats that delay as error and reports a broken model.
    delay = config.hop * (1 + config.lookahead)
    n = len(enhanced) - delay
    return {
        "noisy": example["noisy"],
        "clean": example["clean"],
        "enhanced": enhanced,
        "gated": gated,
        "model": improvement(enhanced[delay : delay + n], example["noisy"][:n], example["clean"][:n]),
        "gate": improvement(gated, example["noisy"], example["clean"]),
        "sr": config.sr,
        "delay_ms": delay / config.sr * 1000.0,
    }


def _mic_loop(model: ExoNS, limit_db: float) -> None:
    try:
        import sounddevice as sd
    except ImportError as exc:
        raise SystemExit("Install the mic extra (sounddevice) to listen live.") from exc
    cleaner = FrameCleaner(model, limit_db=limit_db)

    def callback(indata, outdata, frames, _time, status):
        if status:
            print(status)
        if frames != 960:
            outdata[:] = indata
            return
        interleaved = indata.copy().reshape(-1)
        cleaner.process_inplace(interleaved, limit_db=limit_db)
        outdata[:] = interleaved.reshape(960, 2)

    with sd.Stream(samplerate=48_000, channels=2, dtype="float32", blocksize=960, callback=callback):
        print("Listening. Press Enter to stop.")
        input()


def main() -> None:
    parser = argparse.ArgumentParser(description="Listen to ExoNS on a file, a synthetic mix, or a microphone")
    parser.add_argument("--checkpoint", default=None)
    parser.add_argument("--preset", default="realtime", choices=["realtime", "quality", "tiny"])
    parser.add_argument("--wav", type=Path, default=None)
    parser.add_argument("--out", type=Path, default=Path("exons-clean.wav"))
    parser.add_argument("--synthetic", action="store_true")
    parser.add_argument("--seconds", type=float, default=2.0)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--limit-db", type=float, default=70.0)
    parser.add_argument("--mic", action="store_true")
    args = parser.parse_args()
    model = load_model(args.checkpoint) if args.checkpoint else ExoNS(load_preset(args.preset))
    model.eval()
    if args.mic:
        _mic_loop(model, args.limit_db)
        return
    if args.wav:
        audio = read_audio(args.wav, model.cfg.sr)
        cleaned = clean_wave(model, audio, args.limit_db)
        write_wav(args.out, cleaned, model.cfg.sr)
        print(args.out)
        return
    result = synthetic(model, args.seconds, args.seed, args.limit_db)
    write_wav(args.out, result["enhanced"], result["sr"])
    write_wav(args.out.with_name(args.out.stem + "-noisy.wav"), result["noisy"], result["sr"])
    print(f"model (after {result['delay_ms']:.0f} ms delay)", json_safe(result["model"]))
    print("spectral gate", json_safe(result["gate"]))
    print("wrote", args.out)


def json_safe(scores: dict) -> str:
    return " ".join(f"{key}={value:.2f}" for key, value in scores.items())


if __name__ == "__main__":
    main()
