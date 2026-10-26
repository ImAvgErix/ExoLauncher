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


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint", help="best.pt, or 'random' for an untrained model (tests, benchmarks)")
    ap.add_argument("out")
    ap.add_argument("--int8", action="store_true")
    ap.add_argument("--fp16", action="store_true")
    a = ap.parse_args()
    model = ExoDenoise().eval() if a.checkpoint == "random" else load(a.checkpoint)
    path = export(model, a.out)
    print(path)
    if a.int8:
        print(quantize_int8(path))
    if a.fp16:
        print(convert_fp16(path))


if __name__ == "__main__":
    main()
