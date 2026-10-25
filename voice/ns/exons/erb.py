"""Equivalent-rectangular-bandwidth filterbank.

Rows sum to one, so spreading a vector of band gains back to bins is an
interpolation that leaves a gain of 1 untouched. The scale is the same one
DeepFilterNet uses (Glasberg and Moore).
"""

from __future__ import annotations

import torch


def hz_to_erb(hz: torch.Tensor) -> torch.Tensor:
    return 21.4 * torch.log10(1.0 + 0.00437 * hz)


def erb_to_hz(erb: torch.Tensor) -> torch.Tensor:
    return (10.0 ** (erb / 21.4) - 1.0) / 0.00437


def erb_filterbank(n_freqs: int, sr: int, n_erb: int) -> torch.Tensor:
    """Triangular ERB weights, shape [n_freqs, n_erb], each row summing to 1."""
    freqs = torch.linspace(0.0, sr / 2.0, n_freqs)
    edges = erb_to_hz(torch.linspace(hz_to_erb(freqs.new_tensor(0.0)), hz_to_erb(freqs.new_tensor(sr / 2.0)), n_erb + 2))
    fb = torch.zeros(n_freqs, n_erb)
    for band in range(n_erb):
        left, center, right = edges[band], edges[band + 1], edges[band + 2]
        rising = (freqs - left) / (center - left).clamp_min(1e-8)
        falling = (right - freqs) / (right - center).clamp_min(1e-8)
        fb[:, band] = torch.clamp(torch.minimum(rising, falling), min=0.0)
    row = fb.sum(dim=1, keepdim=True)
    dead = row.squeeze(1) <= 0
    fb = torch.where(row > 0, fb / row.clamp_min(1e-12), fb)
    # A bin that fell in a crack still has to pass a gain of 1.
    if bool(dead.any()):
        fb[dead, :] = 1.0 / n_erb
    return fb
