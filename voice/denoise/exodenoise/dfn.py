"""DeepFilterNet 3 with no lookahead, as a model this pipeline can train, evaluate and export.

DeepFilterNet 3's offline model (Rikorose/DeepFilterNet v0.5.6, MIT/Apache-2.0) looks two frames (20 ms) ahead: its
first convolutions and its deep filter both reach into the future. Its weights don't depend on that: the lookahead is
only padding. Built with both set to zero, it loads the same checkpoint and is causal, a 20 ms model, which starts
far from where the trained one was and is fine-tuned from there with this pipeline's data and losses.

Features are computed here in torch, on libdf's scale (the STFT divided by 960) with its normalisation constant
(0.99), matching libdf's own erb_norm and unit_norm to 1e-6, so the checkpoint sees what it was trained on.
DeepFilterNet's package is imported module by module: its __init__ pulls in an audio I/O layer this needs none of.
"""
from __future__ import annotations

import glob
import importlib.util
import sys
import types
from dataclasses import dataclass
from pathlib import Path

import torch
import torch.nn as nn

from . import dsp

SCALE = dsp.N_FFT  # libdf's spectrum is the plain windowed DFT divided by the FFT size
ALPHA = 0.99  # libdf's norm alpha for a 10 ms hop and 1 s: exp(-0.01) rounded as it rounds it
DFN3_DIR = Path("~/.cache/exodenoise/DeepFilterNet3").expanduser()


def _import_dfn(config_ini: str):
    if "df" not in sys.modules or not hasattr(sys.modules["df"], "__path__"):
        spec = importlib.util.find_spec("df")
        pkg = types.ModuleType("df")
        pkg.__path__ = list(spec.submodule_search_locations)
        sys.modules["df"] = pkg
    from df.config import config

    config.load(config_ini, allow_reload=True)
    config.set("conv_lookahead", 0, int, "deepfilternet")
    config.set("df_lookahead", 0, int, "df")
    from libdf import DF

    from df.deepfilternet3 import init_model

    return init_model(DF(sr=dsp.SR, fft_size=dsp.N_FFT, hop_size=dsp.HOP, nb_bands=32, min_nb_erb_freqs=2))


@dataclass
class DfnConfig:
    kind: str = "dfn3"
    lsnr_min: float = -15.0
    lsnr_max: float = 35.0
    nb_erb: int = 32
    nb_df: int = 96
    df_order: int = 5


class CausalDFN3(nn.Module):
    def __init__(self, cfg: DfnConfig | None = None, pretrained: bool = True):
        super().__init__()
        self.cfg = cfg or DfnConfig()
        self.net = _import_dfn(str(DFN3_DIR / "config.ini"))
        if pretrained:
            ck = glob.glob(str(DFN3_DIR / "checkpoints" / "*"))[0]
            self.net.load_state_dict(torch.load(ck, map_location="cpu", weights_only=False))
        erb_fb, _ = dsp.erb_matrices(self.cfg.nb_erb)
        self.register_buffer("erb_avg", erb_fb, persistent=False)
        self.register_buffer("erb_init", torch.linspace(-60, -90, self.cfg.nb_erb), persistent=False)
        self.register_buffer("df_init", torch.linspace(1e-3, 1e-4, self.cfg.nb_df), persistent=False)

    def features(self, spec: torch.Tensor):
        """libdf-scaled complex (B, T, F) -> erb (B, 1, T, 32), complex low bins (B, 1, T, 96, 2)."""
        b = spec.shape[0]
        db = 10 * torch.log10((spec.real ** 2 + spec.imag ** 2) @ self.erb_avg + 1e-10)
        mag = spec[..., : self.cfg.nb_df].abs()
        mu, s = self.erb_init.expand(b, -1), self.df_init.expand(b, -1)
        erb_n, df_n = [], []
        with torch.no_grad():
            for t in range(spec.shape[1]):
                mu = ALPHA * mu + (1 - ALPHA) * db[:, t]
                s = ALPHA * s + (1 - ALPHA) * mag[:, t]
                erb_n.append(mu)
                df_n.append(s)
        erb = (db - torch.stack(erb_n, 1)) / 40
        low = spec[..., : self.cfg.nb_df] / torch.stack(df_n, 1).sqrt()
        return erb.unsqueeze(1), torch.view_as_real(low).unsqueeze(1)

    def forward(self, spec: torch.Tensor):
        """Same contract as ExoDenoise: complex (B, T, F) on this pipeline's scale -> enhanced, gains, lsnr (B, T)."""
        s = spec / SCALE
        erb, low = self.features(s)
        out, gains, lsnr, _ = self.net(torch.view_as_real(s).unsqueeze(1), erb, low)
        return torch.view_as_complex(out.squeeze(1).contiguous()) * SCALE, gains.squeeze(1), lsnr.squeeze(-1)

    def enhance(self, noisy: torch.Tensor, atten_lim_db: float | None = None) -> torch.Tensor:
        n = noisy.shape[-1]
        x = nn.functional.pad(noisy, (0, (-n) % dsp.HOP + dsp.HOP))
        spec = dsp.stft(x)
        out, _, _ = self(spec)
        if atten_lim_db is not None:
            lim = 10 ** (-atten_lim_db / 20)
            out = out * (1 - lim) + spec * lim
        return dsp.istft(out)[..., :n]

    def config_dict(self) -> dict:
        from dataclasses import asdict

        return asdict(self.cfg)
