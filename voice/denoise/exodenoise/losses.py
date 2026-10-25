"""Training losses. All take enhanced and clean signals aligned sample for sample.

- ``spectral``: DeepFilterNet's compressed spectral loss (magnitudes and complex values raised to 0.3) over several
  STFT resolutions (5 to 40 ms windows), on the waveform the model actually puts out (so STFT inconsistency is paid for).
- ``asym``: the same compressed magnitudes, only where the output is below the clean speech. Over-suppression (lost
  consonants, breath, a voice's dynamics: what carries emotion) costs extra; leftover noise costs the usual.
- ``sisdr``: scale-invariant SDR, a time-domain term that keeps phase honest.
- ``lsnr``: the frame SNR head against the true local SNR from the clean speech and the noise.
"""
from __future__ import annotations

from dataclasses import dataclass

import torch
import torch.nn.functional as F

from . import dsp


@dataclass
class LossConfig:
    spectral: float = 1.0
    asym: float = 1.0
    sisdr: float = 0.02
    lsnr: float = 5e-4
    gamma: float = 0.3
    complex_weight: float = 1.0
    resolutions: tuple[int, ...] = (240, 480, 960, 1920)
    factor: float = 100.0


def _spec(x: torch.Tensor, n: int) -> torch.Tensor:
    w = torch.hann_window(n, device=x.device)
    return torch.stft(x, n, n // 4, window=w, return_complex=True)


def _compress(s: torch.Tensor, gamma: float) -> tuple[torch.Tensor, torch.Tensor]:
    mag = s.abs().clamp_min(1e-12)
    cmag = mag ** gamma
    return cmag, s * (cmag / mag)


def spectral_losses(y: torch.Tensor, x: torch.Tensor, cfg: LossConfig) -> tuple[torch.Tensor, torch.Tensor]:
    spec_loss = y.new_zeros(())
    asym = y.new_zeros(())
    for n in cfg.resolutions:
        ym, yc = _compress(_spec(y, n), cfg.gamma)
        xm, xc = _compress(_spec(x, n), cfg.gamma)
        spec_loss = spec_loss + F.mse_loss(ym, xm) + cfg.complex_weight * F.mse_loss(torch.view_as_real(yc), torch.view_as_real(xc))
        asym = asym + F.relu(xm - ym).pow(2).mean()
    k = cfg.factor / len(cfg.resolutions)
    return spec_loss * k, asym * k


def si_sdr(y: torch.Tensor, x: torch.Tensor, eps: float = 1e-8) -> torch.Tensor:
    """Per-example SI-SDR in dB, (B,)."""
    x = x - x.mean(-1, keepdim=True)
    y = y - y.mean(-1, keepdim=True)
    proj = (y * x).sum(-1, keepdim=True) / (x.pow(2).sum(-1, keepdim=True) + eps) * x
    return 10 * torch.log10(proj.pow(2).sum(-1) / ((y - proj).pow(2).sum(-1) + eps) + eps)


def local_snr(clean_spec: torch.Tensor, noise_spec: torch.Tensor, lo: float, hi: float) -> torch.Tensor:
    """True SNR per frame in dB from complex (B, T, F) spectra, clamped to the head's range."""
    s = clean_spec.abs().pow(2).sum(-1)
    n = noise_spec.abs().pow(2).sum(-1)
    return (10 * torch.log10((s + 1e-10) / (n + 1e-10))).clamp(lo, hi)


def total(y, clean, lsnr_pred, lsnr_true, cfg: LossConfig) -> tuple[torch.Tensor, dict]:
    y, clean = dsp.valid(y), dsp.valid(clean)
    spec, asym = spectral_losses(y, clean, cfg)
    # Silent targets (noise alone) have no SI-SDR: leave them out of that term.
    speech = clean.pow(2).mean(-1) > 1e-8
    sdr = si_sdr(y[speech], clean[speech]).mean() if speech.any() else y.new_zeros(())
    lsnr = F.mse_loss(lsnr_pred, lsnr_true)
    loss = cfg.spectral * spec + cfg.asym * asym - cfg.sisdr * sdr + cfg.lsnr * lsnr
    return loss, {"loss": loss.item(), "spectral": spec.item(), "asym": asym.item(), "sisdr": sdr.item(), "lsnr": lsnr.item()}
