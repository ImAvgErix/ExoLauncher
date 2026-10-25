"""What could fail silently:
- the STFT pair stops reconstructing exactly (a window or framing change): every output is subtly colored;
- streaming drifts from offline (a state slot packed in the wrong order, a missing past frame): training metrics
  stop describing what users hear;
- the exported ONNX graph differs from PyTorch, or the attenuation limit stops being a true bypass;
- the in-stream delay is not one hop: Exo's delay tracker and echo canceller would be off.
"""
import numpy as np
import pytest
import torch

from exodenoise import ExoDenoise, ModelConfig, Streaming, dsp


def test_erb_bands_cover_spectrum():
    widths = dsp.erb_widths()
    assert len(widths) == 32 and sum(widths) == dsp.N_FREQS and min(widths) >= 2


def test_stft_reconstructs():
    x = torch.randn(2, 48000)
    y = dsp.istft(dsp.stft(x))
    assert torch.allclose(dsp.valid(y), dsp.valid(x), atol=1e-5)


def test_dft_matrices_match_stft():
    fwd, _ = dsp.dft_matrices()
    x = torch.randn(1, dsp.N_FFT)
    ri = x @ fwd
    ref = torch.fft.rfft(x * dsp.vorbis_window())
    assert torch.allclose(ri[:, : dsp.N_FREQS], ref.real, atol=1e-4)
    assert torch.allclose(ri[:, dsp.N_FREQS:], ref.imag, atol=1e-4)


def _stream(model, x, atten=100.0):
    s = Streaming(model)
    state = s.initial_state()
    xs = torch.nn.functional.pad(x, (0, dsp.HOP))
    out = []
    with torch.no_grad():
        for i in range(0, xs.shape[1], dsp.HOP):
            y, state, _ = s(xs[:, i: i + dsp.HOP], state, torch.tensor([atten]))
            out.append(y)
    return torch.cat(out, 1)[:, dsp.HOP: dsp.HOP + x.shape[1]]


@pytest.mark.parametrize("cfg", [ModelConfig(), ModelConfig(channels=32, time_hidden=48, blocks=1, df_order=3)])
def test_streaming_equals_offline(cfg):
    torch.manual_seed(0)
    model = ExoDenoise(cfg).eval()
    for p in model.parameters():  # random weights large enough that every path matters
        p.data.normal_(0, 0.3)
    x = torch.randn(1, 24000) * 0.1
    with torch.no_grad():
        offline = model.enhance(x)
    streamed = _stream(model, x)
    n = x.shape[1] - dsp.HOP
    assert torch.allclose(offline[:, :n], streamed[:, :n], atol=1e-4), (offline[:, :n] - streamed[:, :n]).abs().max()


def test_attenuation_limit_zero_is_bypass_delayed_one_hop():
    model = ExoDenoise().eval()
    x = torch.randn(1, 9600) * 0.1
    y = _stream(model, x, atten=0.0)
    assert torch.allclose(y[:, : -dsp.HOP], x[:, : -dsp.HOP], atol=1e-5)


def test_onnx_matches_torch(tmp_path):
    pytest.importorskip("onnxruntime")
    from exodenoise.export import export
    from exodenoise.stream import StreamingDenoiser

    torch.manual_seed(1)
    model = ExoDenoise().eval()
    path = export(model, str(tmp_path))  # export() itself asserts ONNX == PyTorch hop by hop
    den = StreamingDenoiser(str(path))
    x = np.random.default_rng(0).standard_normal(9600).astype(np.float32) * 0.1
    y = den.process_file(x)
    with torch.no_grad():
        ref = model.enhance(torch.from_numpy(x)[None], atten_lim_db=100.0)[0].numpy()
    assert np.abs(y[:-dsp.HOP] - ref[:-dsp.HOP]).max() < 1e-3
    assert np.isfinite(den.lsnr)
