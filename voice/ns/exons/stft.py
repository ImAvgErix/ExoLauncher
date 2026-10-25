"""Causal STFT with a periodic square-root Hann window.

The window is 20 ms and the hop is 10 ms at 48 kHz. Analysis looks only at
samples already received: the streaming buffer starts empty, which is the same
as padding the clip with one hop of zeros on the left. Synthesis is overlap-add
of the same window, divided by the summed window energy, so a gain of 1 and an
identity deep filter reconstruct the input after that hop of delay.

No centered window and no extra lookahead live here. A model lookahead, if the
config asks for one, is applied later when the deep filter reads future frames.
"""

from __future__ import annotations

import torch
import torch.nn as nn
import torch.nn.functional as F


def sqrt_hann(n_fft: int) -> torch.Tensor:
    return torch.hann_window(n_fft, periodic=True).sqrt()


class CausalSTFT(nn.Module):
    def __init__(self, n_fft: int, hop: int):
        super().__init__()
        if hop * 2 != n_fft:
            raise ValueError("this window only overlap-adds at hop = n_fft/2")
        self.n_fft = n_fft
        self.hop = hop
        self.register_buffer("window", sqrt_hann(n_fft), persistent=False)

    @property
    def pad(self) -> int:
        return self.n_fft - self.hop

    def analyze(self, wave: torch.Tensor) -> torch.Tensor:
        """wave [B, N] -> complex spec [B, T, F]. N is padded up to a whole number of hops."""
        wave, _, _ = self._pad(wave)
        frames = wave.unfold(-1, self.n_fft, self.hop)
        return torch.fft.rfft(frames * self.window, dim=-1)

    def synthesize(self, spec: torch.Tensor, length: int) -> torch.Tensor:
        """spec [B, T, F] -> wave [B, length], undoing the left pad used in analyze."""
        frames = torch.fft.irfft(spec, n=self.n_fft, dim=-1) * self.window
        wave = overlap_add(frames, self.hop, self.window)
        start = self.pad
        return wave[:, start : start + length]

    def _pad(self, wave: torch.Tensor) -> tuple[torch.Tensor, int, int]:
        length = wave.shape[-1]
        right = (self.hop - length % self.hop) % self.hop
        padded = F.pad(wave, (self.pad, right))
        return padded, self.pad, length


def overlap_add(frames: torch.Tensor, hop: int, window: torch.Tensor) -> torch.Tensor:
    """frames [B, T, n_fft] already multiplied by the synthesis window."""
    batch, steps, n_fft = frames.shape
    out_len = (steps - 1) * hop + n_fft
    device = frames.device
    slots = torch.arange(n_fft, device=device)
    starts = torch.arange(steps, device=device) * hop
    index = (starts[:, None] + slots[None, :]).reshape(-1)
    mixed = frames.new_zeros(batch, out_len)
    mixed.index_add_(1, index, frames.reshape(batch, -1))
    weight = frames.new_zeros(out_len)
    weight.index_add_(0, index, window.square().repeat(steps))
    return mixed / weight.clamp_min(1e-8)


class StreamSTFT:
    """Hop-by-hop analysis and synthesis. One hop in, one spectrum; one spectrum, one hop out."""

    def __init__(self, n_fft: int, hop: int, window: torch.Tensor):
        self.n_fft = n_fft
        self.hop = hop
        self.window = window
        self.buf: torch.Tensor | None = None
        self.ola: torch.Tensor | None = None
        self.wsum: torch.Tensor | None = None

    def reset(self) -> None:
        self.buf = None
        self.ola = None
        self.wsum = None

    def _ensure(self, hop: torch.Tensor) -> None:
        if self.buf is not None and self.buf.shape[0] == hop.shape[0] and self.buf.device == hop.device:
            return
        batch = hop.shape[0]
        self.buf = hop.new_zeros(batch, self.n_fft)
        self.ola = hop.new_zeros(batch, self.n_fft)
        self.wsum = hop.new_zeros(self.n_fft)

    def analyze(self, hop: torch.Tensor) -> torch.Tensor:
        """hop [B, hop] -> complex spec [B, F] of the window ending at this hop."""
        if hop.shape[-1] != self.hop:
            raise ValueError(f"expected {self.hop} samples, got {hop.shape[-1]}")
        self._ensure(hop)
        assert self.buf is not None
        self.buf = torch.cat([self.buf[:, self.hop :], hop], dim=-1)
        return torch.fft.rfft(self.buf * self.window, dim=-1)

    def synthesize(self, spec: torch.Tensor) -> torch.Tensor:
        """complex spec [B, F] -> hop [B, hop]."""
        assert self.ola is not None and self.wsum is not None
        frame = torch.fft.irfft(spec, n=self.n_fft, dim=-1) * self.window
        self.ola = self.ola + frame
        self.wsum = self.wsum + self.window.square()
        out = self.ola[:, : self.hop] / self.wsum[: self.hop].clamp_min(1e-8)
        tail_ola = self.ola[:, self.hop :]
        tail_w = self.wsum[self.hop :]
        self.ola = torch.cat([tail_ola, torch.zeros_like(self.ola[:, : self.hop])], dim=-1)
        self.wsum = torch.cat([tail_w, torch.zeros_like(self.wsum[: self.hop])], dim=0)
        return out
