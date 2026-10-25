"""Objective scores.

SI-SDR is always available. STOI and PESQ are optional packages (they are the
usual intrusive scores; PESQ is an ITU method with its own license, so it is
not vendored). DNSMOS is Microsoft's non-intrusive P.835 model: download it
yourself for a research eval and pass the ONNX path. It is not shipped here.
"""

from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import torch

from exons.losses import si_sdr


def si_sdr_db(estimate: np.ndarray | torch.Tensor, reference: np.ndarray | torch.Tensor) -> float:
    if not isinstance(estimate, torch.Tensor):
        estimate = torch.as_tensor(estimate, dtype=torch.float32)
    if not isinstance(reference, torch.Tensor):
        reference = torch.as_tensor(reference, dtype=torch.float32)
    estimate = estimate.reshape(1, -1).float()
    reference = reference.reshape(1, -1).float()
    return float(si_sdr(estimate, reference)[0])


def improvement(enhanced: np.ndarray, noisy: np.ndarray, clean: np.ndarray) -> dict[str, float]:
    clean_s = si_sdr_db(enhanced, clean)
    noisy_s = si_sdr_db(noisy, clean)
    out = {"si_sdr": clean_s, "si_sdr_noisy": noisy_s, "si_sdri": clean_s - noisy_s}
    stoi = try_stoi(enhanced, clean)
    if stoi is not None:
        out["stoi"] = stoi
    pesq = try_pesq(enhanced, clean)
    if pesq is not None:
        out["pesq"] = pesq
    return out


def try_stoi(estimate: np.ndarray, reference: np.ndarray, sr: int = 48_000) -> float | None:
    try:
        from pystoi import stoi
    except ImportError:
        return None
    return float(stoi(reference, estimate, sr, extended=False))


def try_pesq(estimate: np.ndarray, reference: np.ndarray, sr: int = 48_000) -> float | None:
    try:
        from pesq import pesq
    except ImportError:
        return None
    # PESQ is defined at 8 and 16 kHz. Wideband uses 16 kHz.
    target = 16_000
    return float(pesq(target, _resample(reference, sr, target), _resample(estimate, sr, target), "wb"))


def _resample(audio: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst:
        return np.asarray(audio, dtype=np.float32)
    n = int(round(len(audio) * dst / src))
    x = np.linspace(0.0, 1.0, len(audio), endpoint=False)
    xp = np.linspace(0.0, 1.0, n, endpoint=False)
    return np.interp(xp, x, np.asarray(audio, dtype=np.float32)).astype(np.float32)


class DNSMOS:
    """P.835 ``sig_bak_ovr.onnx`` from the DNS Challenge, with its published polynomial.

    Microsoft ships that file for non-commercial research. It is not in this
    tree. The model reads 9.01 s of 16 kHz audio (tiled, not zero-padded, when
    the clip is shorter) under the input name ``input_1`` and returns raw SIG,
    BAK and OVRL; the polynomial is the one in ``dnsmos_local.py``.
    """

    def __init__(self, path: str | Path, personalized: bool = False):
        import onnxruntime as ort

        self.session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        self.personalized = personalized

    def __call__(self, audio: np.ndarray, sr: int = 48_000) -> dict[str, float]:
        clip = np.asarray(_resample(audio, sr, 16_000), dtype=np.float32)
        frame = int(9.01 * 16_000)
        if len(clip) == 0:
            raise ValueError("DNSMOS needs audio")
        while len(clip) < frame:
            clip = np.concatenate([clip, clip])
        raw = []
        for start in range(0, int(np.floor(len(clip) / 16_000) - 9.01) + 1):
            piece = clip[start * 16_000 : start * 16_000 + frame]
            if len(piece) < frame:
                continue
            pred = self.session.run(None, {"input_1": piece[None, :]})[0].reshape(-1)
            raw.append(pred[:3])
        if not raw:
            piece = clip[:frame]
            raw.append(self.session.run(None, {"input_1": piece[None, :]})[0].reshape(-1)[:3])
        mean = np.mean(np.stack(raw), axis=0)
        sig, bak, ovl = _dnsmos_poly(float(mean[0]), float(mean[1]), float(mean[2]), self.personalized)
        return {"sig": sig, "bak": bak, "ovl": ovl}


def _dnsmos_poly(sig: float, bak: float, ovl: float, personalized: bool) -> tuple[float, float, float]:
    # Coefficients from Microsoft's dnsmos_local.py, highest degree first.
    if personalized:
        sig_c = (-0.01019296, 0.02751166, 1.19576786, -0.24348726)
        bak_c = (-0.04976499, 0.44276479, -0.1644611, 0.96883132)
        ovl_c = (-0.00533021, 0.005101, 1.18058466, -0.11236046)
    else:
        sig_c = (-0.08397278, 1.22083953, 0.0052439)
        bak_c = (-0.13166888, 1.60915514, -0.39604546)
        ovl_c = (-0.06766283, 1.11546468, 0.04602535)
    return float(np.polyval(sig_c, sig)), float(np.polyval(bak_c, bak)), float(np.polyval(ovl_c, ovl))


def rms_db(audio: np.ndarray) -> float:
    power = float(np.mean(np.square(np.asarray(audio, dtype=np.float64))))
    return 10.0 * math.log10(power + 1e-12)
