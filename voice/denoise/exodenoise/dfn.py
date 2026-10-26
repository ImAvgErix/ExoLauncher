"""DeepFilterNet 3 with no lookahead, as a model this pipeline can train, evaluate and export.

Two starting points (``variant``):
- ``dfn3``: the offline model's checkpoint (2.1 M parameters) with its lookahead removed, see below.
- ``dfn3_ll``: the low-latency model Exo ships today (9.7 M, no lookahead to remove). Only its ONNX export is
  published, so :func:`convert_ll` reads the weights back out of the three graphs into DeepFilterNet's own PyTorch
  modules: named weights by name, the convolutions with their batch norm folded in (the norm set to identity, its
  bias carrying the folded bias), the GRUs from ONNX's z-r-h gate order to PyTorch's r-z-n, and linear layers
  exported as MatMul by their transposed shape. Every output then matches the ONNX graphs to 1e-4 (checked on
  every conversion), so fine-tuning starts exactly where Exo's model is.

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

import numpy as np
import torch
import torch.nn as nn

from . import dsp

SCALE = dsp.N_FFT  # libdf's spectrum is the plain windowed DFT divided by the FFT size
ALPHA = 0.99  # libdf's norm alpha for a 10 ms hop and 1 s: exp(-0.01) rounded as it rounds it
CACHE = Path("~/.cache/exodenoise").expanduser()
DFN3_DIR = CACHE / "DeepFilterNet3"
DFN3_LL_DIR = CACHE / "dfn3_ll"  # DeepFilterNet3_ll_onnx.tar.gz unpacked (baselines.py dfn3_ll fetches it)


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


def _run_order(module: nn.Module, args) -> list:
    """Convolutions, batch norms and GRUs in the order a forward pass runs them."""
    seq, hooks = [], []
    for name, mod in module.named_modules():
        if isinstance(mod, (nn.Conv2d, nn.ConvTranspose2d, nn.GRU, nn.BatchNorm2d)):
            hooks.append(mod.register_forward_hook(lambda m, i, o, n=name: seq.append((n, m))))
    with torch.no_grad():
        module(*args)
    for h in hooks:
        h.remove()
    return seq


def _load_onnx(sub: nn.Module, path: Path, args) -> None:
    import onnx
    from onnx import numpy_helper

    g = onnx.load(str(path)).graph
    w = {i.name: numpy_helper.to_array(i) for i in g.initializer}
    for n in g.node:
        if n.op_type == "Constant":
            w.update({n.output[0]: numpy_helper.to_array(a.t) for a in n.attribute if a.name == "value"})
    sd, new = sub.state_dict(), {}
    new.update({k: torch.from_numpy(w[k].copy()) for k in sd if k in w})
    seq = _run_order(sub, args)
    fused = [(n, m, seq[i + 1]) for i, (n, m) in enumerate(seq)
             if not isinstance(m, (nn.GRU, nn.BatchNorm2d)) and i + 1 < len(seq) and isinstance(seq[i + 1][1], nn.BatchNorm2d)]
    folded = [n.input[1:3] for n in g.node if n.op_type in ("Conv", "ConvTranspose") and n.input[1].startswith("onnx::")]
    assert len(folded) == len(fused), f"{path.name}: {len(folded)} folded convolutions for {len(fused)} conv+norm pairs"
    for (wname, bname), (cname, conv, (nname, norm)) in zip(folded, fused):
        assert w[wname].shape == tuple(conv.weight.shape), (cname, w[wname].shape)
        c = norm.num_features
        new[cname + ".weight"] = torch.from_numpy(w[wname].copy())
        new.update({nname + ".weight": torch.ones(c), nname + ".running_mean": torch.zeros(c),
                    nname + ".running_var": torch.full((c,), 1 - norm.eps), nname + ".bias": torch.from_numpy(w[bname].copy())})
    layers = [(n, i) for n, m in seq if isinstance(m, nn.GRU) for i in range(m.num_layers)]
    nodes = [n for n in g.node if n.op_type == "GRU"]
    assert len(nodes) == len(layers), f"{path.name}: {len(nodes)} GRU nodes for {len(layers)} layers"

    def rzn(a, h):  # ONNX z, r, h -> PyTorch r, z, n
        return torch.from_numpy(np.concatenate([a[h:2 * h], a[:h], a[2 * h:]], 0))

    for node, (n, i) in zip(nodes, layers):
        wi, wh, b = (w[x][0] for x in node.input[1:4])
        h = wh.shape[1]
        new.update({f"{n}.weight_ih_l{i}": rzn(wi, h), f"{n}.weight_hh_l{i}": rzn(wh, h),
                    f"{n}.bias_ih_l{i}": rzn(b[:3 * h], h), f"{n}.bias_hh_l{i}": rzn(b[3 * h:], h)})
    for k in sd:  # linear layers exported as MatMul against their (in, out) weight
        if k not in new and k.endswith(".weight") and sd[k].dim() == 2:
            for n in g.node:
                if n.op_type == "MatMul" and n.input[1] in w and w[n.input[1]].shape == tuple(sd[k].shape[::-1]):
                    new[k] = torch.from_numpy(w[n.input[1]].T.copy())
    missing = [k for k in sd if k not in new and "num_batches" not in k]
    assert not missing, f"{path.name}: no weights for {missing}"
    sub.load_state_dict({**sd, **new})


def convert_ll(net: nn.Module, folder: Path = DFN3_LL_DIR) -> None:
    """DeepFilterNet3_ll's ONNX weights into ``net`` (built from its config.ini), checked against the graphs."""
    import onnxruntime as ort

    t = 50
    fe, fs = torch.randn(1, 1, t, 32), torch.randn(1, 2, t, 96)
    net.eval()
    with torch.no_grad():
        _load_onnx(net.enc, folder / "enc.onnx", (fe, fs))
        e0, e1, e2, e3, emb, c0, lsnr = net.enc(fe, fs)
        _load_onnx(net.erb_dec, folder / "erb_dec.onnx", (emb, e3, e2, e1, e0))
        _load_onnx(net.df_dec, folder / "df_dec.onnx", (emb, c0))
        e0, e1, e2, e3, emb, c0, lsnr = net.enc(fe, fs)
        ref = ort.InferenceSession(str(folder / "enc.onnx")).run(None, {"feat_erb": fe.numpy(), "feat_spec": fs.numpy()})
        ref.append(ort.InferenceSession(str(folder / "erb_dec.onnx")).run(
            None, {"emb": emb.numpy(), "e3": e3.numpy(), "e2": e2.numpy(), "e1": e1.numpy(), "e0": e0.numpy()})[0])
        ref.append(ort.InferenceSession(str(folder / "df_dec.onnx")).run(None, {"emb": emb.numpy(), "c0": c0.numpy()})[0])
        ours = (e0, e1, e2, e3, emb, c0, lsnr, net.erb_dec(emb, e3, e2, e1, e0), net.df_dec(emb, c0))
        worst = max(float(np.abs(r - o.numpy()).max()) for r, o in zip(ref, ours))
    assert worst < 1e-3, f"converted DeepFilterNet3_ll differs from its ONNX graphs by {worst}"


@dataclass
class DfnConfig:
    kind: str = "dfn3"
    variant: str = "dfn3"  # dfn3 (offline checkpoint, lookahead removed) or dfn3_ll (Exo's current model)
    lsnr_min: float = -15.0
    lsnr_max: float = 35.0
    nb_erb: int = 32
    nb_df: int = 96
    df_order: int = 5


class CausalDFN3(nn.Module):
    def __init__(self, cfg: DfnConfig | None = None, pretrained: bool = True):
        super().__init__()
        self.cfg = cfg or DfnConfig()
        ll = self.cfg.variant == "dfn3_ll"
        self.net = _import_dfn(str((DFN3_LL_DIR if ll else DFN3_DIR) / "config.ini"))
        if pretrained and ll:
            convert_ll(self.net)
        elif pretrained:
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
