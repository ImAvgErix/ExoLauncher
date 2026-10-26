"""A fixed noisy/clean test set written to disk, from speech and noise kept out of training:

    python -m exodenoise.data.testset out_dir --speech test_speech.jsonl --noise unseen_noise.jsonl [--n 200]

Mixed by the training mixer with a fixed seed per file, at harder SNRs than VoiceBank-DEMAND's (-5 to 10 dB) and
no rooms, so every system is scored on the same files and none of them has seen the noise.
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import soundfile as sf

from .. import dsp
from . import mixer


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--speech", nargs="+", required=True)
    ap.add_argument("--noise", nargs="+", required=True)
    ap.add_argument("--n", type=int, default=200)
    ap.add_argument("--seconds", type=float, default=4.0)
    ap.add_argument("--snr", type=float, nargs=2, default=(-5.0, 10.0))
    ap.add_argument("--seed", type=int, default=2026)
    a = ap.parse_args()
    cfg = mixer.MixConfig(seconds=a.seconds, snr_db=tuple(a.snr), level_dbfs=(-35, -15), p_rir=0, p_noise_rir=0,
                          p_babble=0.1, p_noise_only=0, p_clean_only=0, p_eq=0.3, p_clip=0, p_lowpass_speech=0)
    m = mixer.Mixer(mixer.load_sources(a.speech, a.noise, []), cfg, a.n, seed=a.seed, fixed=True)
    for d in ("noisy", "clean"):
        (Path(a.out) / d).mkdir(parents=True, exist_ok=True)
    for i in range(a.n):
        noisy, clean = m.mix(np.random.default_rng((a.seed, i)))
        sf.write(Path(a.out) / "noisy" / f"{i:04d}.wav", noisy, dsp.SR, subtype="FLOAT")
        sf.write(Path(a.out) / "clean" / f"{i:04d}.wav", clean, dsp.SR, subtype="FLOAT")
    print(f"{a.n} mixes in {a.out}")


if __name__ == "__main__":
    main()
