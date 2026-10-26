"""ONNX export: python -m exodenoise.export runs/exo-s/best.pt out/ [--int8] [--fp16]

Writes exo-denoise.onnx: one 10 ms hop per call, the whole state in one float vector:
    inputs   hop (1, 480) float32 at 48 kHz, state (1, S), atten_lim_db (1,)
    outputs  out (1, 480) (the previous hop, cleaned), new_state (1, S), lsnr (1,) dB
and the model carries its own description in ONNX metadata (sample_rate, hop, state_size, delay_samples, and
initial_state as space-separated floats), so a runtime needs the one file and hard-codes nothing.
The graph is checked against PyTorch on noise before it is kept. ``--int8`` adds a dynamically quantized copy
(MatMul and GRU weights in int8), ``--fp16`` a half-precision one for GPUs.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
import torch

from . import dsp
from .model import ExoDenoise, ModelConfig, Streaming


def load(checkpoint: str) -> ExoDenoise:
    ck = torch.load(checkpoint, map_location="cpu", weights_only=False)
    if ck["model_config"].get("kind") == "dfn3":
        from .dfn import CausalDFN3, DfnConfig

        model = CausalDFN3(DfnConfig(**ck["model_config"]), pretrained=False)
        model.load_state_dict(ck["model"])
        return model.eval()
    model = ExoDenoise(ModelConfig(**ck["model_config"]))
    model.load_state_dict(ck["model"])
    return model.eval()


def export(model: ExoDenoise, out_dir: str, opset: int = 17) -> Path:
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    stream = Streaming(model).eval()
    state = stream.initial_state()
    args = (torch.zeros(1, dsp.HOP), state, torch.tensor([100.0]))
    path = out / "exo-denoise.onnx"
    torch.onnx.export(stream, args, str(path), input_names=["hop", "state", "atten_lim_db"],
                      output_names=["out", "new_state", "lsnr"], opset_version=opset, dynamo=False)
    describe(path, {"sample_rate": dsp.SR, "hop": dsp.HOP, "state_size": stream.state_size, "delay_samples": dsp.HOP,
                    "initial_state": " ".join(f"{v:.9g}" for v in state[0].tolist()),
                    "layout": json.dumps(stream.layout), "model_config": json.dumps(model.config_dict())})
    check(stream, path)
    return path


def describe(path: Path, meta: dict) -> None:
    import onnx

    m = onnx.load(str(path))
    del m.metadata_props[:]
    for k, v in meta.items():
        m.metadata_props.add(key=k, value=str(v))
    onnx.save(m, str(path))


def _copy_meta(src: Path, dst: Path) -> None:
    import onnx

    describe(dst, {p.key: p.value for p in onnx.load(str(src)).metadata_props})


@torch.no_grad()
def check(stream: Streaming, path: Path, hops: int = 200) -> float:
    import onnxruntime as ort

    sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    rng = np.random.default_rng(0)
    x = (rng.standard_normal((hops, dsp.HOP)) * 0.05).astype(np.float32)
    st_t, st_o = stream.initial_state(), stream.initial_state().numpy()
    lim = np.array([100.0], np.float32)
    worst = 0.0
    for h in x:
        yt, st_t, _ = stream(torch.from_numpy(h[None]), st_t, torch.from_numpy(lim))
        yo, st_o, _ = sess.run(None, {"hop": h[None], "state": st_o, "atten_lim_db": lim})
        worst = max(worst, float(np.abs(yt.numpy() - yo).max()))
    assert worst < 1e-3, f"ONNX differs from PyTorch by {worst}"
    return worst


def quantize_int8(path: Path) -> Path:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    out = path.with_name("exo-denoise.int8.onnx")
    quantize_dynamic(str(path), str(out), weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "Gemm"])
    _copy_meta(path, out)
    return out


def convert_fp16(path: Path) -> Path:
    import onnx
    from onnxruntime.transformers.float16 import convert_float_to_float16

    out = path.with_name("exo-denoise.fp16.onnx")
    # Inputs and outputs stay float32, so a runtime needn't care.
    onnx.save(convert_float_to_float16(onnx.load(str(path)), keep_io_types=True), str(out))
    _copy_meta(path, out)
    return out


def export_dfn(model, out_dir: str) -> Path:
    """A fine-tuned DeepFilterNet (dfn.py) as DeepFilterNet3_ll_onnx.tar.gz: enc, erb_dec and df_dec with the input and
    output names and the dynamic time axis ("S") of DeepFilterNet's own export (df/scripts/export.py, which scripts
    the model with TorchScript first, which current torch can't; these are traced), and config.ini. Exo builds libDF
    with ``--features default-model-ll``, which compiles models/DeepFilterNet3_ll_onnx.tar.gz into
    exo-deepfilter.dll: that file replaced by this one before the build ships the fine-tuned model, nothing else
    changed. Each graph is checked against PyTorch at a length other than the one it was traced at."""
    import shutil
    import tarfile

    import onnxruntime as ort

    from .dfn import DFN3_LL_DIR

    net = model.net.eval()
    out = Path(out_dir)
    graphs = out / "tmp" / "export"
    graphs.mkdir(parents=True, exist_ok=True)

    def one(name, module, inputs, input_names, output_names, axes, check_inputs):
        path = graphs / f"{name}.onnx"
        torch.onnx.export(module, inputs, str(path), input_names=input_names, output_names=output_names,
                          dynamic_axes=axes, opset_version=14, dynamo=False)
        sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        ref = module(*check_inputs)
        ref = ref if isinstance(ref, tuple) else (ref,)
        got = sess.run(None, {n: x.numpy() for n, x in zip(input_names, check_inputs)})
        worst = max(float(np.abs(r.numpy() - g).max()) for r, g in zip(ref, got))
        assert worst < 1e-3, f"{name}.onnx differs from PyTorch by {worst}"
        return ref

    def feats(t):
        return torch.randn(1, 1, t, 32), torch.randn(1, 2, t, 96)

    with torch.no_grad():
        trace, check = feats(100), feats(37)
        enc_axes = {"feat_erb": {2: "S"}, "feat_spec": {2: "S"}, "e0": {2: "S"}, "e1": {2: "S"}, "e2": {2: "S"},
                    "e3": {2: "S"}, "emb": {1: "S"}, "c0": {2: "S"}, "lsnr": {1: "S"}}
        enc_out = ["e0", "e1", "e2", "e3", "emb", "c0", "lsnr"]
        e0, e1, e2, e3, emb, c0, _ = net.enc(*trace)
        ce0, ce1, ce2, ce3, cemb, cc0, _ = one("enc", net.enc, trace, ["feat_erb", "feat_spec"], enc_out, enc_axes, check)
        one("erb_dec", net.erb_dec, (emb, e3, e2, e1, e0), ["emb", "e3", "e2", "e1", "e0"], ["m"],
            {"emb": {1: "S"}, "e3": {2: "S"}, "e2": {2: "S"}, "e1": {2: "S"}, "e0": {2: "S"}, "m": {2: "S"}},
            (cemb, ce3, ce2, ce1, ce0))
        df_out = net.df_dec(emb, c0)
        df_names = ["coefs"] if not isinstance(df_out, tuple) else ["coefs"] + [f"out{i}" for i in range(1, len(df_out))]
        one("df_dec", net.df_dec, (emb, c0), ["emb", "c0"], df_names,
            {"emb": {1: "S"}, "c0": {2: "S"}, "coefs": {1: "S"}}, (cemb, cc0))
    shutil.copyfile(DFN3_LL_DIR / "config.ini", graphs / "config.ini")
    tar = out / "DeepFilterNet3_ll_onnx.tar.gz"
    with tarfile.open(tar, "w:gz") as t:
        for name in ("enc.onnx", "erb_dec.onnx", "df_dec.onnx", "config.ini"):
            t.add(graphs / name, arcname=f"tmp/export/{name}")
    return tar


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint", help="best.pt, or 'random' for an untrained model (tests, benchmarks)")
    ap.add_argument("out")
    ap.add_argument("--int8", action="store_true")
    ap.add_argument("--fp16", action="store_true")
    a = ap.parse_args()
    model = ExoDenoise().eval() if a.checkpoint == "random" else load(a.checkpoint)
    if getattr(model, "cfg", None) is not None and getattr(model.cfg, "kind", "") == "dfn3":
        print(export_dfn(model, a.out))  # DeepFilterNet's own graph format, for Exo's libDF build
        return
    path = export(model, a.out)
    print(path)
    if a.int8:
        print(quantize_int8(path))
    if a.fp16:
        print(convert_fp16(path))


if __name__ == "__main__":
    main()
