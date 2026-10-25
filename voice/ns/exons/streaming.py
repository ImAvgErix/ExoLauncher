"""Frame streaming with the same math as the offline forward.

One 10 ms hop in, one 10 ms hop out. The analysis window is 20 ms of audio
already received. Overlap-add of that window delays an impulse by one hop
(10 ms); the network adds nothing on top when lookahead is 0. A lookahead of
L frames holds the coefficients until the future frames they read have
arrived, which adds L hops. Deep-filter state and the GRU state stay on the
processor, one processor per talker, which is how Exo already runs DeepFilterNet.
"""

from __future__ import annotations

import numpy as np
import torch

from exons.level import frame_db, level_gain, update_level
from exons.model import ExoNS, features, filter_low, mix_attenuation
from exons.stft import StreamSTFT


class StreamProcessor:
    def __init__(
        self,
        model: ExoNS,
        atten_lim_db: float | None = 70.0,
        deep_filter: bool = True,
        stepper=None,
    ):
        self.model = model
        self.cfg = model.cfg
        self.atten_lim_db = atten_lim_db
        self.deep_filter = deep_filter
        self.stepper = stepper or _TorchStepper(model)
        device = next(model.parameters()).device
        dtype = next(model.parameters()).dtype
        self.device = device
        self.dtype = dtype
        self.stft = StreamSTFT(model.cfg.n_fft, model.cfg.hop, model.stft.window.detach())
        self._ready = False
        self.clock = 0
        self.state: dict[str, torch.Tensor] | None = None
        self.cap = model.cfg.df_order + model.cfg.lookahead + 1
        self._masked: torch.Tensor | None = None
        self._noisy: torch.Tensor | None = None
        self._coef: torch.Tensor | None = None
        self._snr: torch.Tensor | None = None

    def reset(self) -> None:
        self.stft.reset()
        self._ready = False
        self.clock = 0
        self.state = None
        self._masked = None
        self._noisy = None
        self._coef = None
        self._snr = None

    def _allocate(self, batch: int) -> None:
        cfg = self.cfg
        device, dtype = self.device, self.dtype
        self.state = self.stepper.initial_state(batch, device, dtype)
        freqs = cfg.n_freqs
        self._masked = torch.zeros(self.cap, batch, freqs, dtype=torch.complex64, device=device)
        self._noisy = torch.zeros(self.cap, batch, freqs, dtype=torch.complex64, device=device)
        self._coef = torch.zeros(self.cap, batch, cfg.df_order, cfg.df_bins, 2, dtype=dtype, device=device)
        self._snr = torch.zeros(self.cap, batch, dtype=dtype, device=device)
        self._ready = True

    @torch.inference_mode()
    def process_hop(self, hop: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """hop [B, hop] or [hop] -> (audio of the same shape, local SNR in dB [B])."""
        squeeze = hop.ndim == 1
        if squeeze:
            hop = hop.unsqueeze(0)
        hop = hop.to(device=self.device, dtype=self.dtype)
        if hop.shape[-1] != self.cfg.hop:
            raise ValueError(f"expected {self.cfg.hop} samples per hop, got {hop.shape[-1]}")
        if not self._ready:
            self._allocate(hop.shape[0])
        assert self.state is not None and self._masked is not None
        spec = self.stft.analyze(hop)
        erb, df = features(spec.unsqueeze(1), self.model.erb_fb, self.cfg.compress, self.cfg.df_bins)
        gain, coef, snr, self.state = self.stepper.step(erb[:, 0], df[:, 0], self.state)
        full = torch.matmul(gain, self.model.erb_fb.transpose(0, 1))
        masked = spec * full.to(spec.dtype)
        slot = self.clock % self.cap
        self._masked[slot] = masked
        self._noisy[slot] = spec
        self._coef[slot] = coef
        self._snr[slot] = snr
        emit = self.clock - self.cfg.lookahead
        self.clock += 1
        if emit < 0:
            audio = hop.new_zeros(hop.shape)
            score = snr.new_full(snr.shape, float("nan"))
            return (audio.squeeze(0), score) if squeeze else (audio, score)
        enhanced = self._enhanced(emit)
        if self.atten_lim_db is not None:
            enhanced = mix_attenuation(enhanced, self._noisy[emit % self.cap], self.atten_lim_db)
        audio = self.stft.synthesize(enhanced)
        score = self._snr[emit % self.cap]
        if squeeze:
            return audio.squeeze(0), score
        return audio, score

    def _enhanced(self, emit: int) -> torch.Tensor:
        assert self._masked is not None and self._coef is not None
        masked = self._masked[emit % self.cap].clone()
        if not self.deep_filter:
            return masked
        order = self.cfg.df_order
        lookahead = self.cfg.lookahead
        frames = []
        for tap in range(order):
            src = emit - tap + lookahead
            if src < 0 or src > self.clock - 1:
                frames.append(torch.zeros_like(masked[..., : self.cfg.df_bins]))
            else:
                frames.append(self._masked[src % self.cap][..., : self.cfg.df_bins])
        # Oldest first, so tap 0 (the newest, src = emit+lookahead) sits at the end.
        stack = torch.stack(list(reversed(frames)), dim=1)
        coef = torch.complex(self._coef[emit % self.cap][..., 0], self._coef[emit % self.cap][..., 1])
        low = filter_low(stack, coef)
        masked[..., : self.cfg.df_bins] = low
        return masked

    def process_wave(self, wave: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """wave [B, N] (N a multiple of hop) -> audio [B, N], snr [B, hops]."""
        if wave.ndim == 1:
            audio, snr = self.process_wave(wave.unsqueeze(0))
            return audio.squeeze(0), snr.squeeze(0)
        hops = wave.shape[-1] // self.cfg.hop
        pieces = []
        scores = []
        for index in range(hops):
            audio, snr = self.process_hop(wave[:, index * self.cfg.hop : (index + 1) * self.cfg.hop])
            pieces.append(audio)
            scores.append(snr)
        return torch.cat(pieces, dim=-1), torch.stack(scores, dim=-1)

    def flush(self, hops: int = 10) -> None:
        """Push silence through, the way a new talker is kept clear of the previous one."""
        device = self.device
        zeros = torch.zeros(1, self.cfg.hop, device=device, dtype=self.dtype)
        for _ in range(hops):
            self.process_hop(zeros)


class _TorchStepper:
    def __init__(self, model: ExoNS):
        self.model = model

    def initial_state(self, batch: int, device: torch.device, dtype: torch.dtype) -> dict[str, torch.Tensor]:
        return self.model.initial_state(batch, device, dtype)

    def step(self, erb, df, state):
        gain, coef, snr, h_fuse, h_erb, h_df, h_inter_erb, h_inter_df = self.model.step(
            erb, df, state["h_fuse"], state["h_erb"], state["h_df"], state["h_inter_erb"], state["h_inter_df"]
        )
        new_state = {
            "h_fuse": h_fuse,
            "h_erb": h_erb,
            "h_df": h_df,
            "h_inter_erb": h_inter_erb,
            "h_inter_df": h_inter_df,
        }
        return gain, coef, snr, new_state


class OnnxStepper:
    """The exported frame step. STFT and the deep filter stay in the processor."""

    NAMES = ("erb", "df", "h_fuse", "h_erb", "h_df", "h_inter_erb", "h_inter_df")

    def __init__(self, session, config):
        self.session = session
        self.cfg = config

    def initial_state(self, batch: int, device: torch.device, dtype: torch.dtype) -> dict[str, torch.Tensor]:
        cfg = self.cfg
        return {
            "h_fuse": torch.zeros(cfg.fuse_layers, batch, cfg.hidden, device=device, dtype=dtype),
            "h_erb": torch.zeros(1, batch, cfg.hidden, device=device, dtype=dtype),
            "h_df": torch.zeros(1, batch, cfg.hidden, device=device, dtype=dtype),
            "h_inter_erb": torch.zeros(cfg.dprnn_blocks, batch, cfg.n_erb, cfg.conv_ch, device=device, dtype=dtype),
            "h_inter_df": torch.zeros(cfg.dprnn_blocks, batch, cfg.df_bins, cfg.conv_ch, device=device, dtype=dtype),
        }

    def step(self, erb, df, state):
        feed = {
            "erb": erb.detach().cpu().numpy(),
            "df": df.detach().cpu().numpy(),
            "h_fuse": state["h_fuse"].detach().cpu().numpy(),
            "h_erb": state["h_erb"].detach().cpu().numpy(),
            "h_df": state["h_df"].detach().cpu().numpy(),
            "h_inter_erb": state["h_inter_erb"].detach().cpu().numpy(),
            "h_inter_df": state["h_inter_df"].detach().cpu().numpy(),
        }
        gain, coef, snr, h_fuse, h_erb, h_df, h_inter_erb, h_inter_df = self.session.run(None, feed)
        device, dtype = erb.device, erb.dtype

        def back(value):
            return torch.from_numpy(value).to(device=device, dtype=dtype)

        new_state = {
            "h_fuse": back(h_fuse),
            "h_erb": back(h_erb),
            "h_df": back(h_df),
            "h_inter_erb": back(h_inter_erb),
            "h_inter_df": back(h_inter_df),
        }
        return back(gain), back(coef), back(snr), new_state


class FrameCleaner:
    """20 ms stereo frames, the contract ``NoiseSuppressor.Process`` already uses.

    Interleaved float32, 960 samples per channel at 48 kHz. The frame is mixed
    to mono, leveled, cleaned as two 10 ms hops, and written back to both
    channels. The return value is the louder hop's local SNR in dB.
    """

    def __init__(self, model: ExoNS, limit_db: float = 70.0, level: bool = True):
        if model.cfg.sr != 48_000 or model.cfg.hop != 480:
            raise ValueError("FrameCleaner expects the 48 kHz, 10 ms model")
        self.proc = StreamProcessor(model, atten_lim_db=limit_db)
        self.level = level
        self.level_db: float | None = None
        self.prev_speech: float | None = None
        self.sr = model.cfg.sr

    def reset(self) -> None:
        self.proc.reset()
        self.level_db = None
        self.prev_speech = None

    def process_inplace(self, interleaved: np.ndarray, limit_db: float | None = None) -> float:
        if limit_db is not None:
            self.proc.atten_lim_db = float(limit_db)
        audio = np.asarray(interleaved, dtype=np.float32)
        if audio.size != 960 * 2:
            raise ValueError("expected 20 ms of interleaved stereo at 48 kHz (1920 floats)")
        stereo = audio.reshape(960, 2)
        mono = stereo.mean(axis=1).copy()
        current = frame_db(mono)
        if self.level:
            self.level_db = update_level(self.level_db, current, self.prev_speech, len(mono), self.sr)
            gain = level_gain(self.level_db)
        else:
            gain = 1.0
        if gain != 1.0:
            mono *= gain
        scores = []
        out = np.empty_like(mono)
        hop = self.proc.cfg.hop
        for index in range(0, len(mono), hop):
            cleaned, snr = self.proc.process_hop(torch.from_numpy(mono[index : index + hop]))
            out[index : index + hop] = cleaned.detach().cpu().numpy()
            value = float(snr.reshape(-1)[0])
            if value == value:
                scores.append(value)
        if gain != 1.0:
            out /= gain
        speech = max(scores) if scores else float("nan")
        self.prev_speech = None if speech != speech else speech
        audio[:] = np.repeat(out[:, None], 2, axis=1).reshape(-1)
        return speech
