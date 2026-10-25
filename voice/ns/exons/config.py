"""Model and training configuration.

The real-time preset uses a 20 ms window and a 10 ms hop with no extra
lookahead. An impulse through that window comes back one hop later (10 ms);
the quality preset adds one frame of lookahead and lands at 20 ms. Both are
inside a 30 ms voice-chat budget. Width changes the hop time, not this delay.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class Config:
    sr: int = 48_000
    n_fft: int = 960
    hop: int = 480
    n_erb: int = 32
    df_bins: int = 96
    df_order: int = 5
    lookahead: int = 0
    conv_ch: int = 48
    conv_layers: int = 2
    dprnn_blocks: int = 1
    hidden: int = 192
    fuse_layers: int = 1
    groups: int = 8
    compress: float = 0.3
    snr_min: float = -15.0
    snr_max: float = 35.0
    # Training mixture. Overridden by a manifest of real files when one is passed.
    snr_low: float = -5.0
    snr_high: float = 20.0
    p_clean: float = 0.10
    p_noise_only: float = 0.05
    p_rir: float = 0.50
    p_tilt: float = 0.50
    w_mr: float = 1.0
    w_oa: float = 1.0
    w_sisdr: float = 0.01
    w_snr: float = 0.02

    def __post_init__(self) -> None:
        if self.hop * 2 != self.n_fft:
            raise ValueError("hop must be n_fft/2 so the periodic Hann window overlaps and adds")
        if self.lookahead < 0:
            raise ValueError("lookahead must be >= 0")
        if self.df_order < 1:
            raise ValueError("df_order must be >= 1")
        if self.df_bins > self.n_fft // 2 + 1:
            raise ValueError("df_bins exceeds the STFT")
        for name, n in (
            ("conv_ch * n_erb", self.conv_ch * self.n_erb),
            ("conv_ch * df_bins", self.conv_ch * self.df_bins),
            ("hidden", self.hidden),
        ):
            if n % self.groups != 0:
                raise ValueError(f"groups={self.groups} must divide {name}={n}")

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict) -> "Config":
        known = {f.name for f in cls.__dataclass_fields__.values()}  # type: ignore[attr-defined]
        return cls(**{k: v for k, v in data.items() if k in known})

    @property
    def n_freqs(self) -> int:
        return self.n_fft // 2 + 1

    @property
    def frame_seconds(self) -> float:
        return self.hop / self.sr

    @property
    def window_seconds(self) -> float:
        return self.n_fft / self.sr


def realtime() -> Config:
    """20 ms window, no extra lookahead. The voice-chat preset."""
    return Config()


def quality() -> Config:
    """One frame of lookahead: 30 ms, a wider dual-path encoder."""
    return Config(lookahead=1, dprnn_blocks=2, conv_ch=64, hidden=256, groups=8)


def tiny() -> Config:
    """Same STFT as the real-time model, a small net for tests."""
    return Config(
        conv_ch=8,
        hidden=16,
        groups=4,
        n_erb=8,
        df_bins=16,
        df_order=3,
        dprnn_blocks=1,
        conv_layers=2,
        fuse_layers=1,
    )


PRESETS = {"realtime": realtime, "quality": quality, "tiny": tiny}


def load_preset(name: str) -> Config:
    try:
        return PRESETS[name]()
    except KeyError as exc:
        raise SystemExit(f"unknown preset {name!r}; choose from {', '.join(PRESETS)}") from exc
