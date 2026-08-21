"""Synthesize Exo Launcher trophy cues.

Short, clean 48 kHz stereo 16-bit PCM with a cosine fade at both ends.
Tiers escalate in register and harmonic richness, not in volume. No
third-party samples.

    python tools/make-trophy-cues.py

Writes ExoLauncher/Assets/Audio/exo-trophy-{bronze,silver,gold,platinum}.wav
and copies the gold cue to exo-achievement-unlock.wav for older installs.
"""
from __future__ import annotations

import math
import struct
import wave
from pathlib import Path

SAMPLE_RATE = 48_000
CHANNELS = 2
TARGET_RMS = 2200.0
PEAK_LIMIT = 20500.0
REPO = Path(__file__).resolve().parent.parent
AUDIO = REPO / "ExoLauncher" / "Assets" / "Audio"

# Bell-ish partials: slight inharmonicity so this is a mallet, not a phone beep.
# (ratio, gain) — same mix for every tier so loudness stays matched.
PARTIALS = (
    (1.0, 1.00),
    (2.003, 0.16),
    (3.01, 0.06),
)

# (duration, fade_in, fade_out, stereo, notes)
# notes: (hz, start, duration, gain) — overlapping strikes, not a jackpot run.
VOICES = {
    "bronze": (0.56, 0.018, 0.14, 0.010, (
        (196.0, 0.00, 0.50, 0.58),
        (293.7, 0.05, 0.44, 0.32),
    )),
    "silver": (0.76, 0.018, 0.16, 0.014, (
        (261.6, 0.00, 0.64, 0.48),
        (392.0, 0.08, 0.58, 0.34),
        (523.3, 0.18, 0.42, 0.18),
    )),
    "gold": (1.02, 0.018, 0.18, 0.016, (
        (392.0, 0.00, 0.82, 0.40),
        (493.9, 0.08, 0.74, 0.30),
        (587.3, 0.16, 0.64, 0.22),
    )),
    "platinum": (1.16, 0.020, 0.24, 0.018, (
        (261.6, 0.00, 1.02, 0.30),
        (329.6, 0.03, 0.98, 0.26),
        (392.0, 0.06, 0.92, 0.22),
        (523.3, 0.12, 0.78, 0.16),
        (659.3, 0.56, 0.38, 0.10),
    )),
}


def raised_cosine(t: float) -> float:
    t = 0.0 if t < 0.0 else 1.0 if t > 1.0 else t
    return 0.5 * (1.0 - math.cos(math.pi * t))


def envelope(time: float, duration: float, fade_in: float, fade_out: float) -> float:
    if time < 0.0 or time >= duration:
        return 0.0
    attack = 1.0 if fade_in <= 0.0 else raised_cosine(min(1.0, time / fade_in))
    remain = duration - time
    release = 1.0 if remain >= fade_out or fade_out <= 0.0 else raised_cosine(remain / fade_out)
    body = math.exp(-1.55 * time / duration)
    return attack * release * body


def strike(local: float, hz: float, duration: float, fade_in: float, fade_out: float, gain: float) -> float:
    env = envelope(local, duration, fade_in, fade_out)
    if env <= 0.0:
        return 0.0
    tone = 0.0
    for ratio, partial_gain in PARTIALS:
        tone += math.sin(2.0 * math.pi * hz * ratio * local) * partial_gain
    return tone * env * gain


def render(name: str) -> tuple[list[float], list[float], float]:
    duration, fade_in, fade_out, stereo, notes = VOICES[name]
    frames = int(math.ceil(SAMPLE_RATE * duration))
    left = [0.0] * frames
    right = [0.0] * frames
    for i in range(frames):
        time = i / SAMPLE_RATE
        mixed = 0.0
        for hz, start, note_dur, gain in notes:
            local = time - start
            if local < 0.0 or local >= note_dur:
                continue
            mixed += strike(local, hz, note_dur, fade_in, fade_out, gain)
        left[i] = mixed * (1.0 + stereo)
        right[i] = mixed * (1.0 - stereo)

    def remove_dc(samples: list[float]) -> None:
        mean = sum(samples) / len(samples)
        for i, value in enumerate(samples):
            samples[i] = value - mean

    remove_dc(left)
    remove_dc(right)

    sum_sq = 0.0
    peak = 0.0
    for a, b in zip(left, right):
        sum_sq += a * a + b * b
        peak = max(peak, abs(a), abs(b))
    rms = math.sqrt(sum_sq / (len(left) * 2)) if left else 0.0
    gain = TARGET_RMS / rms if rms > 1e-9 else 0.0
    if peak * gain > PEAK_LIMIT and peak > 1e-9:
        gain = PEAK_LIMIT / peak
    for i in range(frames):
        left[i] *= gain
        right[i] *= gain
    # Force a silent start and end so a WAV player cannot click.
    left[0] = 0.0
    right[0] = 0.0
    left[-1] = 0.0
    right[-1] = 0.0
    return left, right, duration


def to_pcm16(value: float) -> int:
    return max(-32768, min(32767, int(round(value))))


def write_wav(path: Path, left: list[float], right: list[float]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "w") as wav:
        wav.setnchannels(CHANNELS)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        frames = bytearray()
        for a, b in zip(left, right):
            frames += struct.pack("<hh", to_pcm16(a), to_pcm16(b))
        wav.writeframes(frames)


def measure(path: Path) -> str:
    with wave.open(str(path), "r") as wav:
        nch, sw, sr, nframes, _, _ = wav.getparams()
        raw = wav.readframes(nframes)
    samples = struct.unpack("<" + "h" * (nframes * nch), raw)
    peak = max(abs(s) for s in samples) if samples else 0
    rms = math.sqrt(sum(s * s for s in samples) / len(samples)) if samples else 0.0
    mean = sum(samples) / len(samples) if samples else 0.0
    clipped = sum(1 for s in samples if s in (-32768, 32767))
    peak_db = 20 * math.log10(peak / 32767) if peak else float("-inf")
    rms_db = 20 * math.log10(rms / 32767) if rms else float("-inf")
    dur = nframes / sr
    head = max(abs(s) for s in samples[: int(sr * nch * 0.01)]) if samples else 0
    tail = max(abs(s) for s in samples[-int(sr * nch * 0.01) :]) if samples else 0
    return (
        f"{path.name:28} dur={dur:.3f}s sr={sr} ch={nch} "
        f"peak={peak} ({peak_db:.2f} dBFS) rms={rms:.1f} ({rms_db:.2f} dBFS) "
        f"dc={mean:.2f} clip={clipped} first={samples[0]} last={samples[-1]} "
        f"head10ms={head} tail10ms={tail}"
    )


def main() -> None:
    AUDIO.mkdir(parents=True, exist_ok=True)
    gold_bytes = None
    for name in ("bronze", "silver", "gold", "platinum"):
        left, right, _ = render(name)
        path = AUDIO / f"exo-trophy-{name}.wav"
        write_wav(path, left, right)
        if name == "gold":
            gold_bytes = path.read_bytes()
        print(measure(path))
    if gold_bytes is not None:
        legacy = AUDIO / "exo-achievement-unlock.wav"
        legacy.write_bytes(gold_bytes)
        print(measure(legacy))


if __name__ == "__main__":
    main()
