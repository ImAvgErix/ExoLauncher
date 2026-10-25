"""Training objective.

The multi-resolution compressed spectral loss is the one DeepFilterNet2 and
DPDFNet train with: magnitude and phase, several window lengths, compression
0.3 so quiet bins still matter. The over-attenuation term is DPDFNet's fix for
the failure mode that hurts a voice chat, the model eating the talker. SI-SDR
keeps the waveform from drifting in level and time. The local-SNR term teaches
the gate head the same kind of per-frame voice score Exo already reads.
"""

from __future__ import annotations

import torch
import torch.nn.functional as F

from exons.config import Config
from exons.stft import CausalSTFT


def si_sdr(estimate: torch.Tensor, reference: torch.Tensor, eps: float = 1e-8) -> torch.Tensor:
    """Scale-invariant SDR in dB, shape [B]."""
    reference = reference - reference.mean(dim=-1, keepdim=True)
    estimate = estimate - estimate.mean(dim=-1, keepdim=True)
    dot = (estimate * reference).sum(dim=-1, keepdim=True)
    energy = reference.square().sum(dim=-1, keepdim=True).clamp_min(eps)
    projection = dot * reference / energy
    noise = estimate - projection
    ratio = projection.square().sum(dim=-1) / noise.square().sum(dim=-1).clamp_min(eps)
    return 10.0 * torch.log10(ratio.clamp_min(eps))


def _resolution_losses(estimate: torch.Tensor, reference: torch.Tensor, n_fft: int, compress: float):
    hop = n_fft // 2
    window = torch.hann_window(n_fft, periodic=True, device=estimate.device, dtype=estimate.dtype)
    enhanced = torch.stft(estimate, n_fft, hop, n_fft, window, return_complex=True)
    clean = torch.stft(reference, n_fft, hop, n_fft, window, return_complex=True)
    enhanced_mag = enhanced.abs().clamp_min(1e-8)
    clean_mag = clean.abs().clamp_min(1e-8)
    mag = (enhanced_mag.pow(compress) - clean_mag.pow(compress)).square().mean()
    enhanced_c = enhanced * enhanced_mag.pow(compress - 1.0)
    clean_c = clean * clean_mag.pow(compress - 1.0)
    comp = (enhanced_c - clean_c).abs().square().mean()
    # Where the estimate is quieter than the talker, count the error again.
    under = (clean_mag > enhanced_mag).to(estimate.dtype).detach()
    mag_oa = ((enhanced_mag.pow(compress) - clean_mag.pow(compress)) * under).square().mean()
    comp_oa = ((enhanced_c - clean_c).abs() * under).square().mean()
    return mag + comp, mag_oa + comp_oa


def multi_resolution_loss(estimate: torch.Tensor, reference: torch.Tensor, sr: int, compress: float = 0.3):
    mr = estimate.new_zeros(())
    oa = estimate.new_zeros(())
    for millis in (5, 10, 20, 40):
        n_fft = max(32, int(sr * millis / 1000))
        part_mr, part_oa = _resolution_losses(estimate, reference, n_fft, compress)
        mr = mr + part_mr
        oa = oa + part_oa
    return mr, oa


def oracle_frame_snr(stft: CausalSTFT, speech: torch.Tensor, noise: torch.Tensor, low: float, high: float) -> torch.Tensor:
    speech_e = stft.analyze(speech).abs().square().mean(dim=-1)
    noise_e = stft.analyze(noise).abs().square().mean(dim=-1).clamp_min(1e-12)
    snr = 10.0 * torch.log10((speech_e / noise_e).clamp_min(1e-12))
    return snr.clamp(low, high)


def si_sdr_loss(estimate: torch.Tensor, reference: torch.Tensor) -> torch.Tensor:
    energy = reference.square().mean(dim=-1)
    valid = energy > 1e-6
    if not bool(valid.any()):
        return estimate.new_zeros(())
    return -si_sdr(estimate[valid], reference[valid]).mean()


def compute_losses(
    enhanced: torch.Tensor,
    clean: torch.Tensor,
    pred_snr: torch.Tensor,
    speech: torch.Tensor,
    noise: torch.Tensor,
    stft: CausalSTFT,
    config: Config,
) -> dict[str, torch.Tensor]:
    mr, oa = multi_resolution_loss(enhanced, clean, config.sr, config.compress)
    sdr = si_sdr_loss(enhanced, clean)
    oracle = oracle_frame_snr(stft, speech, noise, config.snr_min, config.snr_max)
    frames = min(pred_snr.shape[-1], oracle.shape[-1])
    snr = F.l1_loss(pred_snr[..., :frames], oracle[..., :frames])
    total = config.w_mr * mr + config.w_oa * oa + config.w_sisdr * sdr + config.w_snr * snr
    return {"loss": total, "mr": mr.detach(), "oa": oa.detach(), "sisdr": sdr.detach(), "snr": snr.detach()}
