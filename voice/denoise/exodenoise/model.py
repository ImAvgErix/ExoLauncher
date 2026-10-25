"""Exo Denoise: a causal, full-band (48 kHz) hybrid of DSP and a small network.

    noisy STFT ─┬─ ERB band powers (32) ─ running mean norm ─ ERB encoder ─┐
                └─ complex bins 0-4.8 kHz (96) ─ running unit norm ─ DF enc ┴─ dual-path core ─┬─ ERB decoder → 32 gains
                                                                                               ├─ DF decoder → 5-tap complex filters
                                                                                               └─ local SNR (dB)

Stage 1 multiplies the whole spectrum by band gains (cheap, takes the noise floor down everywhere). Stage 2 runs a
5-tap complex filter over the last frames of the low bins, where voice harmonics live and a gain alone can't rebuild
them between the partials (DeepFilterNet's deep filtering). The core is a dual-path RNN (DPCRN, GTCRN) over the
encoder's 8 frequency groups: a bidirectional GRU across the groups inside each frame (no time context, so no
latency) and a GRU along time per group. Nothing looks ahead: frame t uses frames <= t only.

``forward`` runs a whole utterance for training; ``Streaming`` runs one 10 ms hop with every bit of memory in one
flat state vector, so a runtime (ONNX Runtime, Exo's C ABI) only carries a float array from call to call.
"""
from __future__ import annotations

import math
from dataclasses import asdict, dataclass

import torch
import torch.nn as nn
import torch.nn.functional as F

from . import dsp


@dataclass
class ModelConfig:
    nb_erb: int = 32
    nb_df: int = 96
    df_order: int = 5
    channels: int = 64
    time_hidden: int = 128
    blocks: int = 2
    norm_tau: float = 1.0  # seconds, the running means' time constant
    lsnr_min: float = -15.0
    lsnr_max: float = 35.0

    @property
    def norm_alpha(self) -> float:
        return math.exp(-dsp.HOP / dsp.SR / self.norm_tau)


class ConvBlock(nn.Module):
    """Conv over (time, freq): time kernel ``kt`` causal (the caller supplies kt-1 past frames), freq stride ``sf``."""

    def __init__(self, cin: int, cout: int, kt: int = 1, kf: int = 3, sf: int = 1):
        super().__init__()
        self.kt = kt
        self.conv = nn.Conv2d(cin, cout, (kt, kf), stride=(1, sf), padding=(0, kf // 2), bias=False)
        self.norm = nn.BatchNorm2d(cout)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return F.relu(self.norm(self.conv(x)))


class UpBlock(nn.Module):
    """Transposed conv doubling the freq axis; time kernel 1, so it holds no state."""

    def __init__(self, c: int):
        super().__init__()
        self.conv = nn.ConvTranspose2d(c, c, (1, 3), stride=(1, 2), padding=(0, 1), output_padding=(0, 1), bias=False)
        self.norm = nn.BatchNorm2d(c)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return F.relu(self.norm(self.conv(x)))


class GroupedLinear(nn.Module):
    """Linear in ``groups`` independent blocks (DeepFilterNet's), 1/groups the weights and MACs of a dense one."""

    def __init__(self, cin: int, cout: int, groups: int):
        super().__init__()
        self.groups = groups
        self.weight = nn.Parameter(torch.empty(groups, cin // groups, cout // groups))
        nn.init.xavier_uniform_(self.weight)

    def forward(self, x: torch.Tensor) -> torch.Tensor:  # (..., cin)
        shape = x.shape[:-1]
        x = x.reshape(-1, self.groups, x.shape[-1] // self.groups).transpose(0, 1)  # (G, N, cin/G)
        y = torch.bmm(x, self.weight).transpose(0, 1)  # (N, G, cout/G)
        return y.reshape(*shape, -1)


class DualPathBlock(nn.Module):
    """Across the frequency groups within a frame (bidirectional: a frame is whole), then along time per group."""

    def __init__(self, c: int, hidden: int):
        super().__init__()
        self.freq_rnn = nn.GRU(c, c // 2, batch_first=True, bidirectional=True)
        self.freq_out = nn.Linear(c, c)
        self.freq_norm = nn.LayerNorm(c)
        self.time_rnn = nn.GRU(c, hidden, batch_first=True)
        self.time_out = nn.Linear(hidden, c)
        self.time_norm = nn.LayerNorm(c)

    def forward(self, x: torch.Tensor, h: torch.Tensor | None) -> tuple[torch.Tensor, torch.Tensor]:
        b, c, t, f = x.shape
        y = x.permute(0, 2, 3, 1).reshape(b * t, f, c)
        y = y + self.freq_norm(self.freq_out(self.freq_rnn(y)[0]))
        y = y.reshape(b, t, f, c).transpose(1, 2).reshape(b * f, t, c)
        r, h = self.time_rnn(y, h)
        y = y + self.time_norm(self.time_out(r))
        return y.reshape(b, f, t, c).permute(0, 3, 2, 1), h


class ExoDenoise(nn.Module):
    def __init__(self, cfg: ModelConfig | None = None):
        super().__init__()
        cfg = cfg or ModelConfig()
        self.cfg = cfg
        c = cfg.channels
        erb_fb, erb_inv = dsp.erb_matrices(cfg.nb_erb)
        self.register_buffer("erb_fb", erb_fb, persistent=False)
        self.register_buffer("erb_inv", erb_inv, persistent=False)
        # Running-norm starting points (DeepFilterNet's): band means from -60 to -90 dB, unit norms from 1e-3 to 1e-4.
        self.register_buffer("erb_norm_init", torch.linspace(-60, -90, cfg.nb_erb), persistent=False)
        self.register_buffer("df_norm_init", torch.linspace(1e-3, 1e-4, cfg.nb_df), persistent=False)

        groups = cfg.nb_erb // 4  # the core's frequency groups (32 -> 16 -> 8)
        self.groups = groups
        self.erb_enc = nn.ModuleList([
            ConvBlock(1, c, kt=3), ConvBlock(c, c, sf=2), ConvBlock(c, c, sf=2), ConvBlock(c, c)])
        self.df_enc = nn.ModuleList([ConvBlock(2, c, kt=3), ConvBlock(c, c, sf=2)])
        self.df_emb = GroupedLinear(c * cfg.nb_df // 2, c * groups, groups)
        self.core = nn.ModuleList([DualPathBlock(c, cfg.time_hidden) for _ in range(cfg.blocks)])
        self.lsnr = nn.Linear(c * groups, 1)

        self.erb_path = nn.ModuleList([nn.Conv2d(c, c, 1) for _ in range(4)])
        self.erb_dec = nn.ModuleList([ConvBlock(c, c), UpBlock(c), UpBlock(c)])
        self.erb_out = nn.Conv2d(c, 1, (1, 3), padding=(0, 1))

        self.df_dec = GroupedLinear(c * groups, cfg.nb_df * cfg.df_order * 2, groups)
        self.df_path = nn.Conv2d(c, cfg.df_order * 2, 1)

    # --- features -------------------------------------------------------------------------------------------------

    def features(self, re: torch.Tensor, im: torch.Tensor, erb_mu: torch.Tensor, df_mu: torch.Tensor):
        """Normalized features for frames (B, T, F) given as real and imaginary parts (no complex dtype, so the
        streaming graph exports), and the running means after them (each (B, bands))."""
        a = self.cfg.norm_alpha
        nb = self.cfg.nb_df
        erb = 10 * torch.log10((re ** 2 + im ** 2) @ self.erb_fb + 1e-10)
        mag = torch.sqrt(re[..., :nb] ** 2 + im[..., :nb] ** 2)
        erb_n, df_n = [], []
        for t in range(re.shape[1]):  # a few hundred tiny steps on inputs only: no autograd graph
            erb_mu = a * erb_mu + (1 - a) * erb[:, t]
            df_mu = a * df_mu + (1 - a) * mag[:, t]
            erb_n.append(erb_mu)
            df_n.append(df_mu)
        erb_feat = (erb - torch.stack(erb_n, 1)) / 40
        scale = torch.stack(df_n, 1).sqrt()
        df_feat = torch.stack([re[..., :nb] / scale, im[..., :nb] / scale], 1)  # (B, 2, T, nb_df)
        return erb_feat.unsqueeze(1), df_feat, erb_mu, df_mu

    # --- network ------------------------------------------------------------------------------------------------------

    def network(self, erb_feat, df_feat, hidden: list[torch.Tensor | None]):
        """Features with their kt-1 past frames already in front -> gains (B,T,nb_erb), DF coefs, lsnr (B,T), hidden."""
        e0 = self.erb_enc[0](erb_feat)
        e1 = self.erb_enc[1](e0)
        e2 = self.erb_enc[2](e1)
        e3 = self.erb_enc[3](e2)
        c0 = self.df_enc[0](df_feat)
        c1 = self.df_enc[1](c0)
        b, ch, t, _ = e3.shape
        emb = self.df_emb(c1.permute(0, 2, 1, 3).reshape(b, t, -1))  # (B, T, C*G), grouped by channel blocks
        z = e3 + emb.reshape(b, t, ch, self.groups).transpose(1, 2)
        new_hidden = []
        for block, h in zip(self.core, hidden):
            z, h = block(z, h)
            new_hidden.append(h)
        flat = z.permute(0, 2, 1, 3).reshape(b, t, -1)
        lsnr = torch.sigmoid(self.lsnr(flat)).squeeze(-1) * (self.cfg.lsnr_max - self.cfg.lsnr_min) + self.cfg.lsnr_min

        d = self.erb_dec[0](self.erb_path[3](e3) + z)
        d = self.erb_dec[1](self.erb_path[2](e2) + d)
        d = self.erb_dec[2](self.erb_path[1](e1) + d)
        gains = torch.sigmoid(self.erb_out(self.erb_path[0](e0) + d)).squeeze(1)  # (B, T, nb_erb)

        coefs = self.df_dec(flat).reshape(b, t, self.cfg.nb_df, -1).permute(0, 3, 1, 2)  # (B, 2*order, T, nb_df)
        coefs = torch.tanh(coefs + self.df_path(c0[:, :, -t:]))
        coefs = coefs.reshape(b, self.cfg.df_order, 2, t, self.cfg.nb_df).permute(0, 3, 4, 1, 2)  # (B,T,nb_df,order,2)
        return gains, coefs, lsnr, new_hidden

    @staticmethod
    def deep_filter(past: torch.Tensor, coefs: torch.Tensor) -> torch.Tensor:
        """past: (B, T + order - 1, nb_df, 2) real/imag, oldest first; coefs (B, T, nb_df, order, 2) -> (B, T, nb_df, 2).
        Tap k multiplies the frame k hops back."""
        order = coefs.shape[3]
        t = coefs.shape[1]
        re = torch.zeros_like(coefs[..., 0, 0])
        im = torch.zeros_like(re)
        for k in range(order):
            x = past[:, order - 1 - k: order - 1 - k + t]
            cr, ci = coefs[..., k, 0], coefs[..., k, 1]
            re = re + x[..., 0] * cr - x[..., 1] * ci
            im = im + x[..., 0] * ci + x[..., 1] * cr
        return torch.stack([re, im], -1)

    def filter_spectrum(self, spec_ri: torch.Tensor, gains, coefs, df_past: torch.Tensor):
        """spec_ri (B,T,F,2); df_past (B, order-1, nb_df, 2): the masked low bins of the frames before these."""
        masked = spec_ri * (gains @ self.erb_inv).unsqueeze(-1)
        low = torch.cat([df_past, masked[:, :, : self.cfg.nb_df]], 1)
        return torch.cat([self.deep_filter(low, coefs), masked[:, :, self.cfg.nb_df:]], 2), low

    def forward(self, spec: torch.Tensor):
        """Complex (B, T, F) noisy spectrum -> enhanced complex spectrum, band gains, local SNR (dB) per frame."""
        b = spec.shape[0]
        erb_mu = self.erb_norm_init.expand(b, -1)
        df_mu = self.df_norm_init.expand(b, -1)
        erb_feat, df_feat, _, _ = self.features(spec.real, spec.imag, erb_mu, df_mu)
        erb_feat = F.pad(erb_feat, (0, 0, 2, 0))
        df_feat = F.pad(df_feat, (0, 0, 2, 0))
        gains, coefs, lsnr, _ = self.network(erb_feat, df_feat, [None] * len(self.core))
        ri = torch.view_as_real(spec)
        past = ri.new_zeros(b, self.cfg.df_order - 1, self.cfg.nb_df, 2)
        out, _ = self.filter_spectrum(ri, gains, coefs, past)
        return torch.view_as_complex(out.contiguous()), gains, lsnr

    def enhance(self, noisy: torch.Tensor, atten_lim_db: float | None = None) -> torch.Tensor:
        """Waveform (B, L) -> enhanced waveform, offline. L is padded to whole hops and cut back."""
        n = noisy.shape[-1]
        x = F.pad(noisy, (0, (-n) % dsp.HOP + dsp.HOP))
        spec = dsp.stft(x)
        out, _, _ = self(spec)
        if atten_lim_db is not None:
            lim = 10 ** (-atten_lim_db / 20)
            out = out * (1 - lim) + spec * lim
        return dsp.istft(out)[..., :n]

    def config_dict(self) -> dict:
        return asdict(self.cfg)


class Streaming(nn.Module):
    """One 10 ms hop: (hop (1, 480), state (1, S), atten_lim_db (1,)) -> (hop out, state, lsnr (1,)).

    The output hop is the input hop before this one, cleaned (10 ms in-stream delay; 20 ms algorithmic latency counting
    the hop's own buffering). ``state`` packs the STFT's last input hop, the synthesis overlap, the running norms, the
    first convs' two past frames, each time GRU's hidden state and the deep filter's past frames.
    """

    def __init__(self, model: ExoDenoise):
        super().__init__()
        self.m = model.eval()
        cfg = model.cfg
        fwd, inv = dsp.dft_matrices()
        self.register_buffer("fwd", fwd, persistent=False)
        self.register_buffer("inv", inv, persistent=False)
        self.layout = {
            "in_buf": dsp.HOP,
            "ola": dsp.HOP,
            "erb_mu": cfg.nb_erb,
            "df_mu": cfg.nb_df,
            "erb_past": 2 * cfg.nb_erb,
            "df_past_feat": 2 * 2 * cfg.nb_df,
            "df_past": (cfg.df_order - 1) * cfg.nb_df * 2,
            **{f"h{i}": model.groups * cfg.time_hidden for i in range(cfg.blocks)},
        }
        self.state_size = sum(self.layout.values())

    def initial_state(self) -> torch.Tensor:
        parts = []
        for name, size in self.layout.items():
            if name == "erb_mu":
                parts.append(self.m.erb_norm_init)
            elif name == "df_mu":
                parts.append(self.m.df_norm_init)
            else:
                parts.append(torch.zeros(size))
        return torch.cat(parts).unsqueeze(0)

    def _split(self, state):
        out, at = {}, 0
        for name, size in self.layout.items():
            out[name] = state[:, at: at + size]
            at += size
        return out

    def forward(self, hop: torch.Tensor, state: torch.Tensor, atten_lim_db: torch.Tensor):
        m, cfg = self.m, self.m.cfg
        s = self._split(state)
        frame = torch.cat([s["in_buf"], hop], 1)  # (1, N)
        ri = frame @ self.fwd  # (1, 2F)
        re, im = ri[:, None, : dsp.N_FREQS], ri[:, None, dsp.N_FREQS:]  # (1, 1, F) each

        erb_feat, df_feat, erb_mu, df_mu = m.features(re, im, s["erb_mu"], s["df_mu"])
        erb_in = torch.cat([s["erb_past"].reshape(1, 1, 2, cfg.nb_erb), erb_feat], 2)
        df_in = torch.cat([s["df_past_feat"].reshape(1, 2, 2, cfg.nb_df), df_feat], 2)
        hidden = [s[f"h{i}"].reshape(1, m.groups, cfg.time_hidden) for i in range(cfg.blocks)]
        gains, coefs, lsnr, hidden = m.network(erb_in, df_in, hidden)

        spec_ri = torch.stack([re, im], -1)
        past = s["df_past"].reshape(1, cfg.df_order - 1, cfg.nb_df, 2)
        out, low = m.filter_spectrum(spec_ri, gains, coefs, past)
        lim = torch.pow(10.0, -atten_lim_db / 20).reshape(1, 1, 1, 1)
        out = out * (1 - lim) + spec_ri * lim

        flat = torch.cat([out[0, 0, :, 0], out[0, 0, :, 1]]).unsqueeze(0)  # (1, 2F)
        y = flat @ self.inv  # (1, N), windowed
        out_hop = s["ola"] + y[:, : dsp.HOP]
        new_state = torch.cat([
            hop,
            y[:, dsp.HOP:],
            erb_mu,
            df_mu,
            erb_in[:, :, 1:].reshape(1, -1),
            df_in[:, :, 1:].reshape(1, -1),
            low[:, 1:].reshape(1, -1),
            *[h.reshape(1, -1) for h in hidden],
        ], 1)
        return out_hop, new_state, lsnr.reshape(1)


def count_params(model: nn.Module) -> int:
    return sum(p.numel() for p in model.parameters())
