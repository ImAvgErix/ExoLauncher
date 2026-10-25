"""Exo Denoise's signal processing: a causal 48 kHz STFT (20 ms Vorbis window, 10 ms hop) and DeepFilterNet's ERB bands.

The window satisfies w(n)^2 + w(n + N/2)^2 = 1, so analysis and synthesis with the same window reconstruct exactly at
50% overlap. Framing is causal: frame t ends where input hop t ends, so a streamed hop comes out one hop (10 ms) later
and nothing ever waits on the future. The DFT is a matrix product, so the exported graph needs no FFT operator and
quantizes like any other MatMul.
"""
from __future__ import annotations

import math

import numpy as np
import torch

SR = 48_000
N_FFT = 960
HOP = 480
N_FREQS = N_FFT // 2 + 1


def vorbis_window(n: int = N_FFT) -> torch.Tensor:
    i = torch.arange(n, dtype=torch.float64)
    return torch.sin(math.pi / 2 * torch.sin(math.pi * (i + 0.5) / n) ** 2).float()


def _freq2erb(f: float) -> float:
    return 9.265 * math.log1p(f / (24.7 * 9.265))


def _erb2freq(e: float) -> float:
    return 24.7 * 9.265 * (math.exp(e / 9.265) - 1)


def erb_widths(nb_bands: int = 32, min_nb_freqs: int = 2, sr: int = SR, n_fft: int = N_FFT) -> list[int]:
    """Bins per ERB band, as libdf computes them (DeepFilterNet), so the bands match the model Exo ships today."""
    width = sr / n_fft
    low, high = _freq2erb(0), _freq2erb(sr / 2)
    step = (high - low) / nb_bands
    widths, prev, over = [], 0, 0
    for i in range(1, nb_bands + 1):
        fb = int(round(_erb2freq(low + i * step) / width))
        nb = fb - prev - over
        if nb < min_nb_freqs:
            over, nb = min_nb_freqs - nb, min_nb_freqs
        else:
            over = 0
        widths.append(nb)
        prev = fb
    widths[-1] += 1
    extra = sum(widths) - (n_fft // 2 + 1)
    if extra > 0:
        widths[-1] -= extra
    assert sum(widths) == n_fft // 2 + 1
    return widths


def erb_matrices(nb_bands: int = 32, min_nb_freqs: int = 2) -> tuple[torch.Tensor, torch.Tensor]:
    """(F, B) averaging matrix for band power and (B, F) spreading matrix for band gains."""
    widths = erb_widths(nb_bands, min_nb_freqs)
    fb = torch.zeros(N_FREQS, nb_bands)
    at = 0
    for b, w in enumerate(widths):
        fb[at:at + w, b] = 1.0
        at += w
    return fb / fb.sum(0, keepdim=True), fb.t().contiguous()


def dft_matrices(n: int = N_FFT) -> tuple[torch.Tensor, torch.Tensor]:
    """Windowed real DFT (n, 2F) and its windowed inverse (2F, n): [re | im] layout."""
    w = vorbis_window(n).double()
    k = torch.arange(N_FREQS, dtype=torch.float64)
    t = torch.arange(n, dtype=torch.float64)
    ang = 2 * math.pi * t[:, None] * k[None, :] / n
    fwd = torch.cat([torch.cos(ang), -torch.sin(ang)], 1) * w[:, None]
    # irfft: x[t] = (1/n) * sum_k c_k (Re X_k cos - Im X_k sin), c_k = 1 for DC and Nyquist, 2 otherwise.
    c = torch.full((N_FREQS,), 2.0, dtype=torch.float64)
    c[0] = c[-1] = 1.0
    inv = torch.cat([c[:, None] * torch.cos(ang.t()), -c[:, None] * torch.sin(ang.t())], 0) / n * w[None, :]
    return fwd.float(), inv.float()


def stft(x: torch.Tensor) -> torch.Tensor:
    """(B, L) with L a multiple of HOP -> complex (B, L / HOP, F). Frame t covers input [t*HOP - HOP, t*HOP + HOP)."""
    x = torch.nn.functional.pad(x, (N_FFT - HOP, 0))
    spec = torch.stft(x, N_FFT, HOP, window=vorbis_window().to(x.device), center=False, return_complex=True)
    return spec.transpose(1, 2)


def istft(spec: torch.Tensor) -> torch.Tensor:
    """Complex (B, T, F) -> (B, T * HOP), aligned with the input of :func:`stft`. The last hop lacks its next frame's
    half and is not exact; losses and metrics leave it out (:func:`valid`)."""
    w = vorbis_window().to(spec.device)
    frames = torch.fft.irfft(spec, n=N_FFT) * w  # (B, T, N)
    first, second = frames[..., :HOP], frames[..., HOP:]
    # Output hop s (input samples [s*HOP, (s+1)*HOP)) = second half of frame s + first half of frame s+1.
    nxt = torch.nn.functional.pad(first[:, 1:], (0, 0, 0, 1))
    return (second + nxt).reshape(spec.shape[0], -1)


def valid(x: torch.Tensor) -> torch.Tensor:
    """The part of a signal :func:`istft` reconstructs exactly (all but the last hop)."""
    return x[..., : x.shape[-1] - HOP]


def resample(x: np.ndarray, sr_in: int, sr_out: int = SR) -> np.ndarray:
    if sr_in == sr_out:
        return x
    from scipy.signal import resample_poly

    g = math.gcd(sr_in, sr_out)
    return resample_poly(x, sr_out // g, sr_in // g, axis=-1).astype(np.float32)
