"""Ways this pipeline can fail, and the checks for them.

The reconstruction can be late, scaled, or non-causal. The streaming state can
disagree with the sequence forward. The attenuation cap can be ignored. A hop
can cost more than the hop. The mixture can miss its SNR. The loss can be NaN,
or a memorized batch can fail to get easier. Export can drop a GRU state.
"""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
import torch

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from exons.baselines import spectral_gate  # noqa: E402
from exons.bench import benchmark, impulse_delay, stft_delay_samples  # noqa: E402
from exons.config import Config, quality, realtime, tiny  # noqa: E402
from exons.data import make_example, mix_at_snr  # noqa: E402
from exons.erb import erb_filterbank  # noqa: E402
from exons.export import export_onnx, open_session, quantize  # noqa: E402
from exons.level import level_gain, update_level  # noqa: E402
from exons.model import ExoNS, count_parameters  # noqa: E402
from exons.streaming import FrameCleaner, OnnxStepper, StreamProcessor  # noqa: E402
from exons.stft import CausalSTFT  # noqa: E402
from exons.train import run, training_step  # noqa: E402


def _align(stream: torch.Tensor, offline: torch.Tensor, hops: int) -> tuple[int, float]:
    best_shift, best = 0, 1e9
    hop = 480
    limit = hops * hop
    stream = stream.detach().flatten().float()
    offline = offline.detach().flatten().float()
    for shift in range(0, limit + 1, hop):
        n = min(stream.numel() - shift, offline.numel())
        if n < hop * 2:
            continue
        err = (stream[shift : shift + n] - offline[:n]).abs().max().item()
        if err < best:
            best, best_shift = err, shift
    return best_shift, best


class SignalTests(unittest.TestCase):
    def test_erb_rows_pass_a_gain_of_one(self):
        bank = erb_filterbank(481, 48_000, 32)
        self.assertTrue(torch.allclose(bank.sum(dim=1), torch.ones(481), atol=1e-5))
        spread = bank @ torch.ones(32)
        self.assertTrue(torch.allclose(spread, torch.ones(481), atol=1e-5))

    def test_stft_reconstructs(self):
        stft = CausalSTFT(960, 480)
        n = 48_000
        t = torch.arange(n) / 48_000
        wave = (0.2 * torch.sin(2 * torch.pi * 220 * t)).view(1, -1)
        back = stft.synthesize(stft.analyze(wave), n)
        err = (back - wave).abs().max().item()
        self.assertLess(err, 1e-4, err)

    def test_stft_delay_is_inside_the_budget(self):
        delay = stft_delay_samples(realtime())
        self.assertGreaterEqual(delay, 0)
        self.assertLessEqual(delay, int(0.030 * 48_000), delay)

    def test_impulse_delay_sign(self):
        source = np.zeros(1000, dtype=np.float32)
        source[100] = 1
        late = np.zeros_like(source)
        late[140] = 1
        self.assertEqual(impulse_delay(late, source), 40)

    def test_mix_hits_the_requested_snr(self):
        rng = np.random.default_rng(0)
        speech = rng.normal(0, 0.1, 48_000).astype(np.float32)
        noise = rng.normal(0, 0.1, 48_000).astype(np.float32)
        mixed, scaled = mix_at_snr(speech, noise, 7.0)
        speech_p = float(np.mean(speech**2))
        noise_p = float(np.mean(scaled**2))
        snr = 10 * np.log10(speech_p / noise_p)
        self.assertAlmostEqual(snr, 7.0, delta=0.05)
        self.assertTrue(np.allclose(mixed, speech + scaled, atol=1e-6))

    def test_leveler_matches_the_voice_chain_rule(self):
        self.assertIsNone(update_level(None, -20.0, None, 960, 48_000))
        self.assertIsNone(update_level(None, -20.0, 5.0, 960, 48_000))
        self.assertEqual(update_level(None, -20.0, 12.0, 960, 48_000), -20.0)
        smoothed = update_level(-20.0, -10.0, 12.0, 960, 48_000)
        self.assertAlmostEqual(smoothed, -20.0 + 10.0 / 150.0, places=6)
        self.assertEqual(update_level(-20.0, -80.0, 12.0, 960, 48_000), -20.0)
        self.assertAlmostEqual(level_gain(None), 1.0)
        self.assertAlmostEqual(level_gain(-4.0), 10 ** (-20 / 20), places=5)
        self.assertAlmostEqual(level_gain(-64.0), 10 ** (40 / 20), places=5)


class ModelTests(unittest.TestCase):
    def test_realtime_size_and_passthrough(self):
        model = ExoNS(realtime())
        count = count_parameters(model)
        self.assertGreater(count, 400_000)
        self.assertLess(count, 3_000_000, count)
        n = 16_000
        t = torch.arange(n) / 48_000
        wave = (0.15 * torch.sin(2 * torch.pi * 180 * t)).view(1, -1)
        model.eval()
        with torch.no_grad():
            out = model.enhance(wave)
        # The heads start as a gain near 1 and an identity deep filter.
        noise = (out - wave).pow(2).mean()
        signal = wave.pow(2).mean().clamp_min(1e-8)
        snr = 10 * torch.log10(signal / noise.clamp_min(1e-12))
        self.assertGreater(float(snr), 20.0, float(snr))

    def test_attenuation_zero_is_the_input(self):
        model = ExoNS(tiny())
        n = 9600
        wave = torch.randn(1, n) * 0.1
        with torch.no_grad():
            out = model.enhance(wave, atten_lim_db=0)
        self.assertLess((out - wave).abs().max().item(), 1e-4)

    def test_streaming_matches_sequence(self):
        torch.manual_seed(2)
        model = ExoNS(tiny())
        model.eval()
        n = 960 * 4
        wave = torch.randn(1, n) * 0.1
        with torch.no_grad():
            offline = model.enhance(wave)
            processor = StreamProcessor(model, atten_lim_db=None)
            streamed, _snr = processor.process_wave(wave)
        shift, err = _align(streamed, offline, hops=4)
        self.assertLess(err, 1e-3, (shift, err))
        self.assertLessEqual(shift, 960 + model.cfg.lookahead * model.cfg.hop)

    def test_lookahead_stream_matches_and_adds_one_hop(self):
        torch.manual_seed(9)
        cfg = Config.from_dict({**tiny().to_dict(), "lookahead": 1})
        model = ExoNS(cfg)
        model.eval()
        wave = torch.randn(1, 960 * 5) * 0.1
        with torch.no_grad():
            offline = model.enhance(wave)
            streamed, _snr = StreamProcessor(model, atten_lim_db=None).process_wave(wave)
        shift, err = _align(streamed, offline, hops=6)
        self.assertLess(err, 1e-4, (shift, err))
        self.assertEqual(shift, 960)

    def test_future_audio_does_not_change_the_past(self):
        torch.manual_seed(3)
        model = ExoNS(tiny())
        model.eval()
        n = 960 * 6
        wave = torch.randn(1, n) * 0.05
        split = 960 * 4
        other = wave.clone()
        other[:, split:] += 0.5
        with torch.no_grad():
            a, _ = StreamProcessor(model, atten_lim_db=None).process_wave(wave)
            b, _ = StreamProcessor(model, atten_lim_db=None).process_wave(other)
        # The window and the deep filter's past are already in the delay.
        guard = model.cfg.n_fft + model.cfg.df_order * model.cfg.hop
        self.assertTrue(torch.allclose(a[:, : split - guard], b[:, : split - guard], atol=1e-5))
        self.assertGreater((a - b).abs().max().item(), 1e-3)

    def test_quality_preset_steps(self):
        model = ExoNS(quality())
        model.eval()
        processor = StreamProcessor(model, atten_lim_db=None)
        hop = torch.randn(model.cfg.hop) * 0.05
        with torch.no_grad():
            # The first lookahead frames have not been emitted yet, so their score is empty.
            held, held_snr = processor.process_hop(hop)
            audio, snr = processor.process_hop(hop)
        self.assertEqual(tuple(audio.shape), (model.cfg.hop,))
        self.assertTrue(torch.isfinite(held).all())
        self.assertFalse(torch.isfinite(held_snr).all())
        self.assertTrue(torch.isfinite(snr).all())
        self.assertTrue(torch.isfinite(audio).all())

    def test_frame_cleaner_contract(self):
        model = ExoNS(tiny())
        cleaner = FrameCleaner(model, limit_db=70)
        frame = np.zeros(1920, dtype=np.float32)
        frame[0::2] = 0.1
        frame[1::2] = 0.1
        speech = cleaner.process_inplace(frame)
        self.assertEqual(frame.shape, (1920,))
        self.assertTrue(np.allclose(frame[0::2], frame[1::2]))
        self.assertTrue(np.isfinite(speech))

    def test_onnx_matches_pytorch_and_quantizes(self):
        torch.manual_seed(4)
        model = ExoNS(tiny())
        model.eval()
        with tempfile.TemporaryDirectory() as tmp:
            path = export_onnx(model, Path(tmp) / "tiny.onnx")
            session = open_session(path, threads=1)
            processor = StreamProcessor(model, atten_lim_db=None, stepper=OnnxStepper(session, model.cfg))
            n = 960 * 3
            wave = torch.randn(1, n) * 0.05
            with torch.no_grad():
                offline = model.enhance(wave)
                streamed, _snr = processor.process_wave(wave)
            _shift, err = _align(streamed, offline, hops=4)
            self.assertLess(err, 2e-3, err)
            quantized = quantize(path, Path(tmp) / "tiny.int8.onnx")
            qsession = open_session(quantized, threads=1)
            qproc = StreamProcessor(model, atten_lim_db=None, stepper=OnnxStepper(qsession, model.cfg))
            with torch.no_grad():
                qout, _ = qproc.process_wave(wave[:, :960])
            self.assertTrue(torch.isfinite(qout).all())

    def test_realtime_is_faster_than_the_hop(self):
        stats = benchmark(ExoNS(realtime()), seconds=1.0, threads=1)
        # One hop of overlap-add. A regression that adds a frame of lookahead lands at 20 ms.
        self.assertLessEqual(stats["latency_ms"], 15.0, stats)
        self.assertGreaterEqual(stats["latency_ms"], 5.0, stats)
        self.assertLess(stats["rtf"], 1.0, stats)
        self.assertLess(stats["ms_per_hop"], stats["hop_ms"], stats)


class TrainTests(unittest.TestCase):
    def test_one_step_is_finite(self):
        torch.manual_seed(5)
        model = ExoNS(tiny())
        example = make_example(np.random.default_rng(5), 9600, 48_000, model.cfg)
        batch = {key: torch.from_numpy(value).unsqueeze(0) for key, value in example.items()}
        optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3)
        stats = training_step(model, batch, optimizer)
        self.assertTrue(np.isfinite(stats["loss"]), stats)

    def test_repeated_batch_gets_easier(self):
        torch.manual_seed(6)
        model = ExoNS(tiny())
        example = make_example(np.random.default_rng(6), 9600, 48_000, model.cfg)
        # Keep drawing until the clip actually contains speech and noise.
        for salt in range(16):
            example = make_example(np.random.default_rng(6 + salt), 9600, 48_000, model.cfg)
            if float(np.mean(example["clean"] ** 2)) > 1e-6 and float(np.mean(example["noise"] ** 2)) > 1e-6:
                break
        batch = {key: torch.from_numpy(value).unsqueeze(0) for key, value in example.items()}
        optimizer = torch.optim.AdamW(model.parameters(), lr=2e-3)
        losses = [training_step(model, batch, optimizer)["loss"] for _ in range(12)]
        self.assertTrue(all(np.isfinite(losses)))
        self.assertLess(losses[-1], losses[0] * 0.95, losses)

    def test_run_writes_a_checkpoint(self):
        with tempfile.TemporaryDirectory() as tmp:
            summary = run(
                tiny(),
                Path(tmp),
                steps=2,
                batch_size=2,
                segment=0.25,
                seed=8,
                device="cpu",
                val_every=2,
                log_every=1,
                overfit=True,
                train_items=2,
                val_items=2,
            )
            self.assertTrue((Path(tmp) / "best.pt").exists())
            rows = [json.loads(line) for line in (Path(tmp) / "metrics.jsonl").read_text(encoding="utf-8").splitlines()]
        self.assertGreaterEqual(summary["steps"], 2)
        self.assertTrue(any("loss" in row for row in rows))
        self.assertTrue(np.isfinite(summary["val_sisdri"]))

    def test_spectral_gate_runs(self):
        rng = np.random.default_rng(7)
        speech = np.sin(2 * np.pi * 200 * np.arange(16_000) / 48_000).astype(np.float32) * 0.2
        noise = rng.normal(0, 0.05, 16_000).astype(np.float32)
        mixed, _ = mix_at_snr(speech, noise, 0.0)
        out = spectral_gate(mixed)
        self.assertEqual(out.shape, mixed.shape)
        self.assertTrue(np.isfinite(out).all())


if __name__ == "__main__":
    unittest.main()
