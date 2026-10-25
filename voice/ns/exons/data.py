"""On-the-fly mixtures.

Real files win when you have them: clean speech, noise, and room impulses from
DNS, LibriVox, EARS, DEMAND, MUSAN, or your own set. With no files, the same
function builds voiced harmonics, fans, keyboards, and babble so the training
loop, the loss, and the tests run anywhere. Synthetic speech is not a claim
about VoiceBank or DNS; it is how the pipeline stays reproducible without a
terabyte download.

The target is the direct sound plus the first 40 ms of the room. Late
reverberation is treated as something to take down, which is the room-echo
half of what a voice-chat denoiser is for, without a second model.
"""

from __future__ import annotations

import json
import math
from pathlib import Path

import numpy as np
import torch

from exons.config import Config


def read_audio(path: str | Path, sr: int) -> np.ndarray:
    path = Path(path)
    audio, file_sr = _decode(path)
    if audio.ndim > 1:
        audio = audio.mean(axis=-1)
    audio = np.asarray(audio, dtype=np.float32)
    if int(file_sr) != sr:
        audio = resample(audio, int(file_sr), sr)
    return audio


def _decode(path: Path) -> tuple[np.ndarray, int]:
    try:
        import soundfile as sf

        audio, file_sr = sf.read(str(path), always_2d=False, dtype="float32")
        return np.asarray(audio, dtype=np.float32), int(file_sr)
    except Exception:
        pass
    import wave

    with wave.open(str(path), "rb") as handle:
        sr = handle.getframerate()
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        frames = handle.readframes(handle.getnframes())
    if width == 2:
        audio = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
    elif width == 4:
        audio = np.frombuffer(frames, dtype=np.int32).astype(np.float32) / 2147483648.0
    elif width == 1:
        audio = (np.frombuffer(frames, dtype=np.uint8).astype(np.float32) - 128.0) / 128.0
    else:
        raise ValueError(f"unsupported wav width {width} in {path}")
    if channels > 1:
        audio = audio.reshape(-1, channels)
    return audio, sr


def resample(audio: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst:
        return np.asarray(audio, dtype=np.float32)
    try:
        from scipy.signal import resample_poly

        g = math.gcd(src, dst)
        return resample_poly(audio, dst // g, src // g).astype(np.float32)
    except Exception:
        n = int(round(len(audio) * dst / src))
        tensor = torch.from_numpy(np.asarray(audio, dtype=np.float32)).view(1, 1, -1)
        return torch.nn.functional.interpolate(tensor, size=n, mode="linear", align_corners=False).view(-1).numpy()


def load_manifest(path: str | Path | None) -> dict[str, list[str]]:
    if path is None:
        return {"speech": [], "noise": [], "rir": []}
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return {
        "speech": list(data.get("speech", [])),
        "noise": list(data.get("noise", [])),
        "rir": list(data.get("rir", [])),
    }


def crop(rng: np.random.Generator, audio: np.ndarray, n: int) -> np.ndarray:
    audio = np.asarray(audio, dtype=np.float32).reshape(-1)
    if len(audio) == 0:
        return np.zeros(n, dtype=np.float32)
    if len(audio) < n:
        audio = np.tile(audio, int(math.ceil(n / len(audio))))
    start = int(rng.integers(0, len(audio) - n + 1))
    out = audio[start : start + n]
    return (out - out.mean()).astype(np.float32)


def match_rms(audio: np.ndarray, reference: np.ndarray) -> np.ndarray:
    src = math.sqrt(float(np.mean(np.square(audio))) + 1e-12)
    dst = math.sqrt(float(np.mean(np.square(reference))) + 1e-12)
    return (audio * (dst / src)).astype(np.float32)


def mix_at_snr(speech: np.ndarray, noise: np.ndarray, snr_db: float) -> tuple[np.ndarray, np.ndarray]:
    speech_p = float(np.mean(np.square(speech))) + 1e-12
    noise_p = float(np.mean(np.square(noise))) + 1e-12
    scale = math.sqrt(speech_p / (10 ** (snr_db / 10.0)) / noise_p)
    scaled = (noise * scale).astype(np.float32)
    return (speech + scaled).astype(np.float32), scaled


def convolve_same(audio: np.ndarray, impulse: np.ndarray) -> np.ndarray:
    n = len(audio)
    nfft = 1 << (n + len(impulse) - 2).bit_length()
    mixed = np.fft.irfft(np.fft.rfft(audio, nfft) * np.fft.rfft(impulse, nfft), nfft)
    return mixed[:n].astype(np.float32)


def synth_speech(rng: np.random.Generator, n: int, sr: int) -> np.ndarray:
    t = np.arange(n, dtype=np.float64) / sr
    f0 = float(rng.uniform(90, 240))
    vibrato = 1.0 + 0.03 * np.sin(2 * math.pi * float(rng.uniform(3, 6)) * t)
    phase = 2 * math.pi * np.cumsum(f0 * vibrato) / sr
    harmonics = int(rng.integers(8, 14))
    tilt = float(rng.uniform(0.7, 1.2))
    ks = np.arange(1, harmonics + 1, dtype=np.float64)[:, None]
    offsets = rng.uniform(0, math.pi, size=harmonics)[:, None]
    wave = ((ks ** -tilt) * np.sin(ks * phase[None, :] + offsets)).sum(axis=0)
    wave = _formants(wave, sr, rng)
    syllable = np.clip(np.sin(2 * math.pi * float(rng.uniform(2.5, 5.5)) * t + float(rng.uniform(0, 6))), 0, None)
    syllable = syllable ** 1.4
    fric = np.diff(rng.normal(0, 1, n).astype(np.float64), prepend=0)
    burst = (rng.random(n) < 0.02).astype(np.float64)
    burst = np.convolve(burst, np.hanning(max(4, sr // 400)), mode="same")
    wave = wave * (0.15 + syllable) + 0.15 * fric * burst
    return _unit(wave)


def _formants(audio: np.ndarray, sr: int, rng: np.random.Generator) -> np.ndarray:
    spec = np.fft.rfft(audio)
    freqs = np.fft.rfftfreq(len(audio), d=1.0 / sr)
    shape = np.zeros(len(freqs), dtype=np.float64)
    for _ in range(3):
        center = float(rng.uniform(300, 3200))
        bw = float(rng.uniform(80, 250))
        shape += np.exp(-0.5 * ((freqs - center) / bw) ** 2)
    return np.fft.irfft(spec * (0.2 + shape), len(audio))


def synth_noise(rng: np.random.Generator, n: int, sr: int) -> np.ndarray:
    kind = str(rng.choice(["white", "pink", "brown", "fan", "keyboard", "babble"]))
    if kind == "white":
        return _unit(rng.normal(0, 1, n))
    if kind == "pink":
        return _unit(_colored(rng, n, 0.5))
    if kind == "brown":
        return _unit(_colored(rng, n, 1.0))
    if kind == "fan":
        t = np.arange(n) / sr
        hum = 0.4 * np.sin(2 * math.pi * float(rng.uniform(40, 120)) * t)
        hum += 0.15 * np.sin(2 * math.pi * float(rng.uniform(150, 300)) * t)
        return _unit(_colored(rng, n, 0.7) + hum)
    if kind == "keyboard":
        return _unit(_keyboard(rng, n, sr))
    voices = np.zeros(n, dtype=np.float64)
    for _ in range(int(rng.integers(2, 5))):
        voice = synth_speech(rng, n, sr).astype(np.float64)
        shift = int(rng.integers(0, n))
        voices += 0.4 * np.roll(voice, shift)
    return _unit(voices)


def _colored(rng: np.random.Generator, n: int, slope: float) -> np.ndarray:
    spec = np.fft.rfft(rng.normal(0, 1, n))
    freqs = np.fft.rfftfreq(n, d=1.0)
    freqs[0] = freqs[1] if len(freqs) > 1 else 1.0
    spec = spec / (freqs ** slope)
    return np.fft.irfft(spec, n)


def _keyboard(rng: np.random.Generator, n: int, sr: int) -> np.ndarray:
    out = np.zeros(n, dtype=np.float64)
    pos = 0
    while pos < n:
        pos += int(rng.uniform(0.03, 0.18) * sr)
        if pos >= n:
            break
        dur = int(rng.uniform(0.004, 0.012) * sr)
        freq = float(rng.uniform(1200, 4500))
        env = np.exp(-np.linspace(0, 8, dur))
        click = np.sin(2 * math.pi * freq * np.arange(dur) / sr) * env
        end = min(n, pos + dur)
        out[pos:end] += click[: end - pos]
    return out


def _unit(audio: np.ndarray) -> np.ndarray:
    audio = np.asarray(audio, dtype=np.float32)
    rms = math.sqrt(float(np.mean(np.square(audio))) + 1e-12)
    return (audio / rms * 0.1).astype(np.float32)


def synth_rir(rng: np.random.Generator, sr: int) -> np.ndarray:
    rt60 = float(rng.uniform(0.15, 0.7))
    n = max(8, int(rt60 * sr))
    t = np.arange(n) / sr
    decay = np.exp(-6.907 * t / rt60).astype(np.float32)
    impulse = rng.normal(0, 1, n).astype(np.float32) * decay
    impulse[0] = 1.0
    return impulse


def early_part(impulse: np.ndarray, sr: int, millis: float = 40.0) -> np.ndarray:
    n = min(len(impulse), int(sr * millis / 1000))
    out = np.zeros_like(impulse)
    out[:n] = impulse[:n]
    return out


def spectral_tilt(rng: np.random.Generator, audio: np.ndarray) -> np.ndarray:
    spec = np.fft.rfft(audio)
    cut = int(rng.integers(max(2, len(spec) // 10), max(3, len(spec) // 2)))
    spec[cut:] *= float(rng.uniform(0.15, 1.0))
    pre = float(rng.uniform(-0.4, 0.4))
    tilted = np.fft.irfft(spec, len(audio)).astype(np.float32)
    emphasized = np.diff(tilted, prepend=tilted[:1])
    return (tilted + pre * emphasized).astype(np.float32)


def _pick(rng: np.random.Generator, files: list[str], n: int, sr: int, synth) -> np.ndarray:
    if not files:
        return synth(rng, n, sr)
    return crop(rng, read_audio(files[int(rng.integers(0, len(files)))], sr), n)


def make_example(
    rng: np.random.Generator,
    n: int,
    sr: int,
    config: Config,
    speech_files: list[str] | None = None,
    noise_files: list[str] | None = None,
    rir_files: list[str] | None = None,
) -> dict[str, np.ndarray]:
    speech_files = speech_files or []
    noise_files = noise_files or []
    rir_files = rir_files or []
    dry = _pick(rng, speech_files, n, sr, synth_speech)
    if rng.random() < config.p_tilt:
        dry = spectral_tilt(rng, dry)
    target = dry
    carried = dry
    if rng.random() < config.p_rir:
        if rir_files:
            impulse = crop(rng, read_audio(rir_files[int(rng.integers(0, len(rir_files)))], sr), int(0.8 * sr))
        else:
            impulse = synth_rir(rng, sr)
        carried = match_rms(convolve_same(dry, impulse), dry)
        target = match_rms(convolve_same(dry, early_part(impulse, sr)), dry)
    noise = _pick(rng, noise_files, n, sr, synth_noise)
    draw = float(rng.random())
    if draw < config.p_clean:
        noisy = carried
        noise_used = np.zeros_like(carried)
    elif draw < config.p_clean + config.p_noise_only:
        target = np.zeros_like(carried)
        noisy = noise
        noise_used = noise
    else:
        snr = float(rng.uniform(config.snr_low, config.snr_high))
        noisy, noise_used = mix_at_snr(carried, noise, snr)
    noise_only = config.p_clean <= draw < config.p_clean + config.p_noise_only
    speech_used = np.zeros_like(carried) if noise_only else carried
    gain = 10 ** (float(rng.uniform(-12, 6)) / 20.0)
    noisy = noisy * gain
    target = target * gain
    noise_used = noise_used * gain
    speech_used = speech_used * gain
    peak = max(float(np.max(np.abs(noisy))), float(np.max(np.abs(target))), 1e-6)
    if peak > 0.99:
        scale = 0.99 / peak
        noisy = noisy * scale
        target = target * scale
        noise_used = noise_used * scale
        speech_used = speech_used * scale
    return {
        "noisy": noisy.astype(np.float32),
        "clean": target.astype(np.float32),
        "speech": speech_used.astype(np.float32),
        "noise": noise_used.astype(np.float32),
    }


class MixDataset(torch.utils.data.Dataset):
    def __init__(
        self,
        config: Config,
        n: int,
        segment: float,
        seed: int,
        manifest: dict[str, list[str]] | None = None,
        fixed: bool = False,
    ):
        self.config = config
        self.n = n
        self.samples = int(segment * config.sr)
        self.seed = seed
        self.manifest = manifest or {"speech": [], "noise": [], "rir": []}
        self.epoch = 0
        self.fixed = fixed

    def __len__(self) -> int:
        return self.n

    def __getitem__(self, idx: int) -> dict[str, torch.Tensor]:
        salt = 0 if self.fixed else self.epoch * self.n
        rng = np.random.default_rng(self.seed + salt + int(idx))
        example = make_example(
            rng,
            self.samples,
            self.config.sr,
            self.config,
            self.manifest["speech"],
            self.manifest["noise"],
            self.manifest["rir"],
        )
        return {key: torch.from_numpy(value) for key, value in example.items()}


def collate(batch: list[dict[str, torch.Tensor]]) -> dict[str, torch.Tensor]:
    return {key: torch.stack([item[key] for item in batch]) for key in batch[0]}
