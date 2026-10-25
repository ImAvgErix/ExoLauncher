"""Causal full-band noise suppressor.

The enhancement path is DeepFilterNet's, because it is the open design that
actually stays real-time at 48 kHz: a coarse ERB gain for the speech envelope,
then a short complex filter on the bins below 4.8 kHz where pitch lives. The
encoder adds the dual-path block DPDFNet uses, so each branch can look across
frequency inside the frame and across time at each band, without a look at the
future. Temporal convolution kernels are 1, so the only things carried from
frame to frame are the GRU states.

The last layer is initialized as a pass-through (ERB gain near 1, deep-filter
tap 0 equal to 1). Training starts from the noisy signal instead of from
silence, which is what keeps quiet speech and emotion from being trained away.
"""

from __future__ import annotations

import torch
import torch.nn as nn

from exons.config import Config
from exons.erb import erb_filterbank
from exons.stft import CausalSTFT


class GroupedLinear(nn.Module):
    def __init__(self, in_features: int, out_features: int, groups: int):
        super().__init__()
        if in_features % groups or out_features % groups:
            raise ValueError(f"groups={groups} must divide {in_features} and {out_features}")
        self.conv = nn.Conv1d(in_features, out_features, kernel_size=1, groups=groups)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        lead = x.shape[:-1]
        y = self.conv(x.reshape(-1, x.shape[-1], 1)).squeeze(-1)
        return y.reshape(*lead, -1)


class ConvFreq(nn.Module):
    """Frequency-only separable conv. Time kernel is 1, so a frame does not see its neighbors."""

    def __init__(self, channels_in: int, channels_out: int):
        super().__init__()
        self.depth = nn.Conv2d(channels_in, channels_in, (1, 3), padding=(0, 1), groups=channels_in)
        self.point = nn.Conv2d(channels_in, channels_out, 1)
        self.norm = nn.LayerNorm(channels_out)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        y = self.point(self.depth(x))
        y = self.norm(y.permute(0, 2, 3, 1)).permute(0, 3, 1, 2)
        return torch.relu(y)


class DPRNNBlock(nn.Module):
    """Intra-frame bidirectional GRU over frequency, then a causal GRU over time."""

    def __init__(self, channels: int):
        super().__init__()
        self.intra = nn.GRU(channels, channels, batch_first=True, bidirectional=True)
        self.intra_proj = nn.Linear(channels * 2, channels)
        self.intra_norm = nn.LayerNorm(channels)
        self.inter = nn.GRU(channels, channels, batch_first=True)
        self.inter_norm = nn.LayerNorm(channels)

    def forward(self, x: torch.Tensor, state: torch.Tensor | None) -> tuple[torch.Tensor, torch.Tensor]:
        # x: [B, C, T, F]. state: [B, F, C] or None.
        batch, channels, steps, freqs = x.shape
        intra = x.permute(0, 2, 3, 1).reshape(batch * steps, freqs, channels)
        intra, _ = self.intra(intra)
        intra = self.intra_norm(self.intra_proj(intra))
        x = x + intra.reshape(batch, steps, freqs, channels).permute(0, 3, 1, 2)

        inter = x.permute(0, 3, 2, 1).reshape(batch * freqs, steps, channels)
        if state is None:
            inter_out, hidden = self.inter(inter)
        else:
            hidden0 = state.reshape(1, batch * freqs, channels)
            inter_out, hidden = self.inter(inter, hidden0)
        inter_out = self.inter_norm(inter_out)
        x = x + inter_out.reshape(batch, freqs, steps, channels).permute(0, 3, 2, 1)
        return x, hidden.reshape(batch, freqs, channels)


class EncoderBranch(nn.Module):
    def __init__(self, channels_in: int, channels: int, layers: int, blocks: int):
        super().__init__()
        convs: list[nn.Module] = []
        cin = channels_in
        for _ in range(layers):
            convs.append(ConvFreq(cin, channels))
            cin = channels
        self.convs = nn.Sequential(*convs)
        self.blocks = nn.ModuleList(DPRNNBlock(channels) for _ in range(blocks))

    def forward(self, x: torch.Tensor, state: torch.Tensor | None) -> tuple[torch.Tensor, torch.Tensor]:
        x = self.convs(x)
        states = []
        for index, block in enumerate(self.blocks):
            hidden = None if state is None else state[index]
            x, hidden_n = block(x, hidden)
            states.append(hidden_n)
        stacked = torch.stack(states, dim=0) if states else x.new_zeros(0)
        return x, stacked


class ExoNS(nn.Module):
    def __init__(self, config: Config | None = None):
        super().__init__()
        self.cfg = config or Config()
        cfg = self.cfg
        self.stft = CausalSTFT(cfg.n_fft, cfg.hop)
        self.register_buffer("erb_fb", erb_filterbank(cfg.n_freqs, cfg.sr, cfg.n_erb), persistent=False)
        self.erb_enc = EncoderBranch(1, cfg.conv_ch, cfg.conv_layers, cfg.dprnn_blocks)
        self.df_enc = EncoderBranch(2, cfg.conv_ch, cfg.conv_layers, cfg.dprnn_blocks)
        self.erb_proj = GroupedLinear(cfg.conv_ch * cfg.n_erb, cfg.hidden, cfg.groups)
        self.df_proj = GroupedLinear(cfg.conv_ch * cfg.df_bins, cfg.hidden, cfg.groups)
        self.fuse_norm = nn.LayerNorm(cfg.hidden * 2)
        self.fuse = nn.GRU(cfg.hidden * 2, cfg.hidden, num_layers=cfg.fuse_layers, batch_first=True)
        self.erb_gru = nn.GRU(cfg.hidden, cfg.hidden, batch_first=True)
        self.df_gru = nn.GRU(cfg.hidden, cfg.hidden, batch_first=True)
        self.gain_local = nn.Linear(cfg.conv_ch, 1)
        self.gain_global = nn.Linear(cfg.hidden, cfg.n_erb)
        self.df_local = nn.Linear(cfg.conv_ch, cfg.df_order * 2)
        self.df_global = nn.Linear(cfg.hidden, cfg.df_order * 2)
        self.snr_head = nn.Linear(cfg.hidden, 1)
        self._init_passthrough()

    def _init_passthrough(self) -> None:
        for linear in (self.gain_local, self.gain_global, self.df_local, self.df_global, self.snr_head):
            nn.init.normal_(linear.weight, std=1e-3)
            nn.init.zeros_(linear.bias)
        # sigmoid(5) is about 0.993, so an untrained model barely touches the envelope.
        nn.init.constant_(self.gain_global.bias, 5.0)

    def initial_state(self, batch: int, device: torch.device, dtype: torch.dtype = torch.float32) -> dict[str, torch.Tensor]:
        cfg = self.cfg
        return {
            "h_fuse": torch.zeros(cfg.fuse_layers, batch, cfg.hidden, device=device, dtype=dtype),
            "h_erb": torch.zeros(1, batch, cfg.hidden, device=device, dtype=dtype),
            "h_df": torch.zeros(1, batch, cfg.hidden, device=device, dtype=dtype),
            "h_inter_erb": torch.zeros(cfg.dprnn_blocks, batch, cfg.n_erb, cfg.conv_ch, device=device, dtype=dtype),
            "h_inter_df": torch.zeros(cfg.dprnn_blocks, batch, cfg.df_bins, cfg.conv_ch, device=device, dtype=dtype),
        }

    def forward(
        self,
        noisy: torch.Tensor,
        atten_lim_db: float | None = None,
        deep_filter: bool = True,
    ) -> tuple[torch.Tensor, torch.Tensor]:
        """noisy [B, N] -> (enhanced [B, N], local SNR in dB [B, T])."""
        noisy = noisy.to(device=self.erb_fb.device, dtype=self.erb_fb.dtype)
        length = noisy.shape[-1]
        spec = self.stft.analyze(noisy)
        enhanced, snr, _state = self.forward_spec(spec, atten_lim_db=atten_lim_db, deep_filter=deep_filter)
        return self.stft.synthesize(enhanced, length), snr

    def forward_spec(
        self,
        spec: torch.Tensor,
        state: dict[str, torch.Tensor] | None = None,
        atten_lim_db: float | None = None,
        deep_filter: bool = True,
    ):
        erb, df = features(spec, self.erb_fb, self.cfg.compress, self.cfg.df_bins)
        gain, coef, snr, new_state = self.forward_net(erb, df, state)
        if deep_filter:
            enhanced = apply_spectrum(spec, gain, coef, self.erb_fb, self.cfg.df_bins, self.cfg.lookahead)
        else:
            enhanced = spec * torch.matmul(gain, self.erb_fb.transpose(0, 1)).to(spec.dtype)
        if atten_lim_db is not None:
            enhanced = mix_attenuation(enhanced, spec, atten_lim_db)
        return enhanced, snr, new_state

    def enhance(self, noisy: torch.Tensor, atten_lim_db: float | None = None, deep_filter: bool = True) -> torch.Tensor:
        wave, _snr = self.forward(noisy, atten_lim_db=atten_lim_db, deep_filter=deep_filter)
        return wave

    def forward_net(
        self,
        erb: torch.Tensor,
        df: torch.Tensor,
        state: dict[str, torch.Tensor] | None = None,
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, dict[str, torch.Tensor]]:
        """erb [B, T, E], df [B, T, F, 2] -> gain [B, T, E], coef [B, T, O, F, 2], snr [B, T], state."""
        batch, steps, _erb_bands = erb.shape
        if state is None:
            state = self.initial_state(batch, erb.device, erb.dtype)
        erb_map, h_inter_erb = self.erb_enc(erb.unsqueeze(1), state["h_inter_erb"])
        df_map, h_inter_df = self.df_enc(df.permute(0, 3, 1, 2), state["h_inter_df"])
        flat_e = erb_map.permute(0, 2, 1, 3).reshape(batch, steps, -1)
        flat_d = df_map.permute(0, 2, 1, 3).reshape(batch, steps, -1)
        fused = self.fuse_norm(torch.cat([self.erb_proj(flat_e), self.df_proj(flat_d)], dim=-1))
        fused, h_fuse = self.fuse(fused, state["h_fuse"])
        erb_h, h_erb = self.erb_gru(fused, state["h_erb"])
        df_h, h_df = self.df_gru(fused, state["h_df"])
        gain = self._gains(erb_map, erb_h)
        coef = self._coefs(df_map, df_h)
        snr = self._snr(fused)
        new_state = {
            "h_fuse": h_fuse,
            "h_erb": h_erb,
            "h_df": h_df,
            "h_inter_erb": h_inter_erb,
            "h_inter_df": h_inter_df,
        }
        return gain, coef, snr, new_state

    def step(
        self,
        erb: torch.Tensor,
        df: torch.Tensor,
        h_fuse: torch.Tensor,
        h_erb: torch.Tensor,
        h_df: torch.Tensor,
        h_inter_erb: torch.Tensor,
        h_inter_df: torch.Tensor,
    ):
        """One frame. erb [B, E], df [B, F, 2]. Returns the frame outputs and the new states."""
        state = {
            "h_fuse": h_fuse,
            "h_erb": h_erb,
            "h_df": h_df,
            "h_inter_erb": h_inter_erb,
            "h_inter_df": h_inter_df,
        }
        gain, coef, snr, new_state = self.forward_net(erb.unsqueeze(1), df.unsqueeze(1), state)
        return (
            gain[:, 0],
            coef[:, 0],
            snr[:, 0],
            new_state["h_fuse"],
            new_state["h_erb"],
            new_state["h_df"],
            new_state["h_inter_erb"],
            new_state["h_inter_df"],
        )

    def _gains(self, erb_map: torch.Tensor, hidden: torch.Tensor) -> torch.Tensor:
        local = self.gain_local(erb_map.permute(0, 2, 3, 1)).squeeze(-1)
        return torch.sigmoid(local + self.gain_global(hidden))

    def _coefs(self, df_map: torch.Tensor, hidden: torch.Tensor) -> torch.Tensor:
        local = self.df_local(df_map.permute(0, 2, 3, 1))
        coef = local + self.df_global(hidden)[:, :, None, :]
        batch, steps, freqs, _ = coef.shape
        coef = coef.reshape(batch, steps, freqs, self.cfg.df_order, 2).permute(0, 1, 3, 2, 4)
        ident = torch.zeros_like(coef)
        ident[:, :, 0, :, 0] = 1
        return coef + ident

    def _snr(self, hidden: torch.Tensor) -> torch.Tensor:
        raw = self.snr_head(hidden).squeeze(-1)
        span = self.cfg.snr_max - self.cfg.snr_min
        return self.cfg.snr_min + span * torch.sigmoid(raw)


def features(spec: torch.Tensor, erb_fb: torch.Tensor, compress: float, df_bins: int) -> tuple[torch.Tensor, torch.Tensor]:
    """spec [B, T, F] complex -> ERB log-power [B, T, E], compressed low bins [B, T, df_bins, 2]."""
    mag = spec.abs().clamp_min(1e-8)
    band = torch.matmul(mag.square(), erb_fb)
    # Fixed shift, not a statistic of the clip, so a frame's feature does not depend on the rest.
    erb = (torch.log10(band.clamp_min(1e-12)) + 8.0) / 4.0
    compressed = spec * mag.pow(compress - 1.0)
    low = compressed[..., :df_bins]
    df = torch.stack((low.real, low.imag), dim=-1)
    return erb, df


def shift_frames(x: torch.Tensor, steps: int) -> torch.Tensor:
    """Shift time right by `steps` (positive looks at the past). x is [B, T, F] complex."""
    if steps == 0:
        return x
    if steps > 0:
        pad = x.new_zeros(x.shape[0], steps, x.shape[-1])
        return torch.cat((pad, x[:, :-steps]), dim=1)
    steps = -steps
    pad = x.new_zeros(x.shape[0], steps, x.shape[-1])
    return torch.cat((x[:, steps:], pad), dim=1)


def apply_spectrum(
    spec: torch.Tensor,
    gain: torch.Tensor,
    coef: torch.Tensor,
    erb_fb: torch.Tensor,
    df_bins: int,
    lookahead: int,
) -> torch.Tensor:
    """Apply ERB gains, then the deep filter on the low bins of the gained spectrum."""
    full = torch.matmul(gain, erb_fb.transpose(0, 1))
    masked = spec * full.to(spec.dtype)
    low = masked[..., :df_bins]
    coef_c = torch.complex(coef[..., 0], coef[..., 1])
    acc = torch.zeros_like(low)
    order = coef.shape[2]
    for tap in range(order):
        acc = acc + coef_c[:, :, tap, :] * shift_frames(low, tap - lookahead)
    out = masked.clone()
    out[..., :df_bins] = acc
    return out


def filter_low(frames: torch.Tensor, coef: torch.Tensor) -> torch.Tensor:
    """Deep filter one emitted frame.

    frames is [B, order, F] complex, oldest first, newest last. coef is
    [B, order, F] complex. Tap 0 multiplies the newest frame, tap 1 the one
    before it. Zeros stand in for frames that have not arrived.
    """
    if frames.shape[1] != coef.shape[1]:
        raise ValueError("deep filter frame stack and coefficients differ in order")
    acc = torch.zeros(frames.shape[0], frames.shape[2], dtype=frames.dtype, device=frames.device)
    for tap in range(coef.shape[1]):
        acc = acc + coef[:, tap, :] * frames[:, -(tap + 1), :]
    return acc


def mix_attenuation(enhanced: torch.Tensor, noisy: torch.Tensor, limit_db: float) -> torch.Tensor:
    """Cap suppression. 0 dB is the noisy signal; a large limit is the enhanced signal."""
    floor = 10 ** (-float(limit_db) / 20.0)
    return enhanced * (1.0 - floor) + noisy * floor


def count_parameters(module: nn.Module) -> int:
    return sum(p.numel() for p in module.parameters() if p.requires_grad)
