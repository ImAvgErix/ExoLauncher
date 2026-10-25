"""On-the-fly mixing: every training example is a new draw of speech, noise, room and level.

What a mix can be, and why (each probability is in the config's ``mix`` section):
- speech from any corpus at any rate, brought to 48 kHz. Band-limited speech stays band-limited in the target, so the
  model learns to keep what is there and never to invent highs.
- a room: speech convolved with a measured or simulated RIR. The target keeps the direct sound and the first 50 ms
  (``early_ms``), so late reverb counts as noise but the voice's own room color stays: removing all of it sounds
  unnatural on a headset mic.
- noise: one to three noise clips, sometimes in a room of their own, plus, at ``p_babble``, another person talking
  well under the speaker (background speech is noise in a voice chat).
- SNR from ``snr_db`` (DNS-style, -5 to 35 dB), noise alone at ``p_noise_only`` (target: silence), clean speech
  alone at ``p_clean_only`` (target: itself: the model must never harm a clean mic).
- a mic: random peaking and shelving EQ on the speech (target included: a mic's color is the speaker's, not noise),
  a level from -45 to -10 dBFS for the whole mix, and rare clipping of the mix alone.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np
import soundfile as sf
import torch
from scipy.signal import fftconvolve, lfilter

from .. import dsp
from . import manifest


@dataclass
class MixConfig:
    seconds: float = 3.0
    snr_db: tuple[float, float] = (-5.0, 35.0)
    level_dbfs: tuple[float, float] = (-45.0, -10.0)
    p_rir: float = 0.5
    p_noise_rir: float = 0.3
    early_ms: float = 50.0
    p_babble: float = 0.2
    babble_below_db: tuple[float, float] = (5.0, 20.0)
    max_noises: int = 3
    p_noise_only: float = 0.05
    p_clean_only: float = 0.05
    p_eq: float = 0.5
    p_clip: float = 0.03
    p_lowpass_speech: float = 0.1  # a full-band clip brought down to 8/16/24 kHz and back, like a phone or a codec


@dataclass
class Sources:
    speech: list[dict]
    noise: list[dict]
    rir: list[dict] = field(default_factory=list)


def _read(row: dict, frames: int, rng: np.random.Generator, loop: bool = False) -> np.ndarray:
    """``frames`` samples at 48 kHz from a random place in the file (looped when shorter and ``loop``)."""
    sr = row["sr"]
    need = int(math.ceil(frames * sr / dsp.SR)) + 16
    total = row["frames"]
    start = int(rng.integers(0, max(1, total - need)))
    x, _ = sf.read(row["path"], start=start, stop=min(total, start + need), dtype="float32", always_2d=True)
    x = x[:, int(rng.integers(0, x.shape[1]))]  # one channel of a multichannel recording
    x = dsp.resample(x, sr)
    if len(x) < frames:
        x = np.tile(x, int(math.ceil(frames / max(1, len(x))))) if loop and len(x) else np.pad(x, (0, frames - len(x)))
    return x[:frames]


def _rms(x: np.ndarray) -> float:
    return float(np.sqrt(np.mean(x.astype(np.float64) ** 2) + 1e-12))


def _biquad(kind: str, f0: float, gain_db: float, q: float):
    a = 10 ** (gain_db / 40)
    w = 2 * math.pi * f0 / dsp.SR
    alpha = math.sin(w) / (2 * q)
    cw = math.cos(w)
    if kind == "peak":
        b = [1 + alpha * a, -2 * cw, 1 - alpha * a]
        den = [1 + alpha / a, -2 * cw, 1 - alpha / a]
    else:  # shelves (RBJ cookbook)
        sa = 2 * math.sqrt(a) * alpha
        if kind == "low":
            b = [a * ((a + 1) - (a - 1) * cw + sa), 2 * a * ((a - 1) - (a + 1) * cw), a * ((a + 1) - (a - 1) * cw - sa)]
            den = [(a + 1) + (a - 1) * cw + sa, -2 * ((a - 1) + (a + 1) * cw), (a + 1) + (a - 1) * cw - sa]
        else:
            b = [a * ((a + 1) + (a - 1) * cw + sa), -2 * a * ((a - 1) + (a + 1) * cw), a * ((a + 1) + (a - 1) * cw - sa)]
            den = [(a + 1) - (a - 1) * cw + sa, 2 * ((a - 1) - (a + 1) * cw), (a + 1) - (a - 1) * cw - sa]
    return np.array(b) / den[0], np.array(den) / den[0]


def random_eq(x: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    for kind, lo, hi in (("low", 60, 300), ("peak", 300, 6000), ("high", 3000, 12000)):
        if rng.random() < 0.6:
            b, a = _biquad(kind, float(rng.uniform(lo, hi)), float(rng.uniform(-8, 8)), float(rng.uniform(0.5, 2.0)))
            x = lfilter(b, a, x).astype(np.float32)
    return x


def _lowpass(x: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    sr = int(rng.choice([16000, 24000, 32000]))
    return dsp.resample(dsp.resample(x, dsp.SR, sr), sr, dsp.SR)[: len(x)]


def _reverb(x: np.ndarray, rir: np.ndarray, early_ms: float) -> tuple[np.ndarray, np.ndarray]:
    """(reverberant, early-only) speech, both aligned to the RIR's direct path."""
    peak = int(np.argmax(np.abs(rir)))
    rir = rir[peak:] / (np.abs(rir[peak]) + 1e-9)
    early = rir[: int(early_ms / 1000 * dsp.SR)]
    n = len(x)
    return fftconvolve(x, rir)[:n].astype(np.float32), fftconvolve(x, early)[:n].astype(np.float32)


class Mixer(torch.utils.data.Dataset):
    """Each index is a mix. ``fixed=True`` makes index i the same mix every time (validation)."""

    def __init__(self, sources: Sources, cfg: MixConfig, length: int, seed: int = 0, fixed: bool = False):
        self.s, self.cfg, self.length, self.seed, self.fixed = sources, cfg, length, seed, fixed

    def __len__(self) -> int:
        return self.length

    def _rir(self, rng):
        # Read each time, not cached: a worker that kept every RIR it drew grew to gigabytes over a run of 60k RIRs.
        row = self.s.rir[int(rng.integers(len(self.s.rir)))]
        x, sr = sf.read(row["path"], frames=int(row["sr"]), dtype="float32", always_2d=True)  # at most 1 s
        return dsp.resample(x[:, 0], sr)

    def _speech(self, rng, frames):
        for _ in range(10):  # skip near-silent crops
            x = _read(self.s.speech[int(rng.integers(len(self.s.speech)))], frames, rng)
            if _rms(x) > 1e-4:
                return x
        return x

    def mix(self, rng: np.random.Generator) -> tuple[np.ndarray, np.ndarray]:
        c = self.cfg
        n = int(c.seconds * dsp.SR) // dsp.HOP * dsp.HOP
        speech = self._speech(rng, n)
        if rng.random() < c.p_lowpass_speech:
            speech = _lowpass(speech, rng)
        if rng.random() < c.p_eq:
            speech = random_eq(speech, rng)
        target = speech
        if self.s.rir and rng.random() < c.p_rir:
            speech, target = _reverb(speech, self._rir(rng), c.early_ms)

        noise = np.zeros(n, np.float32)
        for _ in range(int(rng.integers(1, c.max_noises + 1))):
            x = _read(self.s.noise[int(rng.integers(len(self.s.noise)))], n, rng, loop=True)
            if self.s.rir and rng.random() < c.p_noise_rir:
                x, _ = _reverb(x, self._rir(rng), c.early_ms)
            if rng.random() < c.p_eq:
                x = random_eq(x, rng)
            noise += x / _rms(x) * float(10 ** (rng.uniform(-6, 0) / 20))
        if rng.random() < c.p_babble:
            other = self._speech(rng, n)
            if self.s.rir and rng.random() < c.p_rir:
                other, _ = _reverb(other, self._rir(rng), c.early_ms)
            noise += other / _rms(other) * _rms(noise) * float(10 ** (-rng.uniform(*c.babble_below_db) / 20))

        r = rng.random()
        if r < c.p_noise_only:
            speech, target = speech * 0, target * 0
            noise *= 0.1 / _rms(noise)
        elif r < c.p_noise_only + c.p_clean_only:
            noise *= 0
        else:
            snr = rng.uniform(*c.snr_db)
            noise *= _rms(speech) / _rms(noise) * float(10 ** (-snr / 20))
        noisy = speech + noise
        gain = float(10 ** (rng.uniform(*c.level_dbfs) / 20)) / _rms(noisy)
        noisy, target = noisy * gain, target * gain
        peak = np.abs(noisy).max()
        if peak > 0.99:  # no accidental clipping: scale down
            noisy, target = noisy * 0.99 / peak, target * 0.99 / peak
        if rng.random() < c.p_clip:
            drive = float(10 ** (rng.uniform(3, 12) / 20))
            noisy = np.clip(noisy * drive, -0.99, 0.99) / drive
        return noisy.astype(np.float32), target.astype(np.float32)

    def __getitem__(self, i: int):
        seed = (self.seed, i) if self.fixed else (self.seed, i, int(torch.randint(0, 2**31 - 1, (1,))))
        noisy, clean = self.mix(np.random.default_rng(seed))
        return torch.from_numpy(noisy), torch.from_numpy(clean)


def load_sources(speech: list[str], noise: list[str], rir: list[str]) -> Sources:
    rows = lambda paths: [r for p in paths for r in manifest.read(p)]  # noqa: E731
    return Sources(rows(speech), rows(noise), rows(rir))


class PairedSet(torch.utils.data.Dataset):
    """A fixed test set of (noisy, clean) files matched by name (VoiceBank-DEMAND, DNS synthetic test sets)."""

    def __init__(self, noisy_dir: str, clean_dir: str, limit: int | None = None):
        from pathlib import Path

        self.pairs = [(p, Path(clean_dir) / p.name) for p in sorted(Path(noisy_dir).glob("*.wav"))]
        self.pairs = [p for p in self.pairs if p[1].exists()][:limit]

    def __len__(self) -> int:
        return len(self.pairs)

    def __getitem__(self, i: int):
        noisy, sr = sf.read(str(self.pairs[i][0]), dtype="float32")
        clean, sr2 = sf.read(str(self.pairs[i][1]), dtype="float32")
        n = min(len(noisy), len(clean))
        return torch.from_numpy(dsp.resample(noisy[:n], sr)), torch.from_numpy(dsp.resample(clean[:n], sr2)), self.pairs[i][0].stem
