"""Reference suppressors for the same clips.

The spectral gate is a causal stationary tracker we ship, so a training run
has something weaker to beat without downloading anyone else's weights.
DeepFilterNet, Facebook's Denoiser and RNNoise are imported only when those
packages are installed. Closed SDKs (Krisp, NVIDIA Maxine) are not called:
their weights are not in this tree. If you have a license, run them offline
and train against the wavs they write (``--manifest`` of already-mixed pairs
is the open half of that; a teacher directory of ``*.clean.wav`` next to
``*.noisy.wav`` is the distillation half).
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import torch

from exons.stft import CausalSTFT


def spectral_gate(noisy: np.ndarray, sr: int = 48_000, floor: float = 0.1) -> np.ndarray:
    """Causal Wiener-style gate. Noise is a slow envelope of bins that look like noise."""
    wave = torch.from_numpy(np.asarray(noisy, dtype=np.float32)).view(1, -1)
    stft = CausalSTFT(960 if sr == 48_000 else 320, 480 if sr == 48_000 else 160)
    spec = stft.analyze(wave)
    mag = spec.abs()
    noise = torch.zeros_like(mag)
    tracker = mag[:, 0].clone()
    gated = []
    for t in range(mag.shape[1]):
        frame = mag[:, t]
        # Rise slowly, fall quickly: a stationary floor that still notices a new fan.
        tracker = torch.minimum(tracker * 1.01 + 1e-4, torch.maximum(tracker * 0.9, frame))
        # Bins much louder than the floor are speech and do not train the tracker as hard.
        stationary = frame < tracker * 1.5
        tracker = torch.where(stationary, 0.95 * tracker + 0.05 * frame, tracker)
        noise[:, t] = tracker
        gain = (1.0 - (tracker / frame.clamp_min(1e-8)).square()).clamp(floor, 1.0)
        gated.append(spec[:, t] * gain)
    enhanced = torch.stack(gated, dim=1)
    out = stft.synthesize(enhanced, wave.shape[-1])
    return out.squeeze(0).numpy()


def try_deepfilter(noisy: np.ndarray, sr: int = 48_000) -> np.ndarray | None:
    try:
        from df.enhance import enhance, init_df
    except ImportError:
        return None
    model, state, _ = init_df()
    audio = torch.from_numpy(np.asarray(noisy, dtype=np.float32)).view(1, -1)
    if sr != state.sr():
        return None
    return enhance(model, state, audio).squeeze(0).cpu().numpy()


def try_denoiser(noisy: np.ndarray, sr: int = 48_000) -> np.ndarray | None:
    """Facebook Denoiser (Demucs). Offline: it looks ahead, so it is a teacher, not the live path."""
    try:
        from denoiser import pretrained
        from denoiser.dsp import convert_audio
    except ImportError:
        return None
    model = pretrained.dns64()
    model.eval()
    wav = torch.from_numpy(np.asarray(noisy, dtype=np.float32)).view(1, 1, -1)
    wav = convert_audio(wav, sr, model.sample_rate, model.chin)
    with torch.no_grad():
        out = model(wav)[0]
    if model.sample_rate != sr:
        out = torch.nn.functional.interpolate(out.unsqueeze(0), size=len(noisy), mode="linear", align_corners=False)[0]
    return out.squeeze(0).cpu().numpy()


def load_teacher_pairs(directory: str | Path) -> list[tuple[Path, Path]]:
    root = Path(directory)
    pairs = []
    for noisy in sorted(root.glob("*.noisy.wav")):
        clean = noisy.with_name(noisy.name.replace(".noisy.wav", ".clean.wav"))
        if clean.exists():
            pairs.append((noisy, clean))
    return pairs
