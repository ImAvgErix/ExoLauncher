"""Export the per-frame network to ONNX and optionally quantize it.

The STFT, the ERB spread and the deep filter stay outside the graph. They are
small, and keeping them in PyTorch/NumPy makes the streaming state obvious.
What ONNX runs is the part that costs: one frame of the encoder, the GRUs and
the two heads. Batch size is 1, which is one talker; Exo already gives each
voice its own suppressor.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import torch
import torch.nn as nn

from exons.model import ExoNS
from exons.train import load_model


class FrameStep(nn.Module):
    def __init__(self, model: ExoNS):
        super().__init__()
        self.model = model

    def forward(self, erb, df, h_fuse, h_erb, h_df, h_inter_erb, h_inter_df):
        return self.model.step(erb, df, h_fuse, h_erb, h_df, h_inter_erb, h_inter_df)


INPUTS = ("erb", "df", "h_fuse", "h_erb", "h_df", "h_inter_erb", "h_inter_df")
OUTPUTS = ("gain", "coef", "snr", "h_fuse_n", "h_erb_n", "h_df_n", "h_inter_erb_n", "h_inter_df_n")


def export_onnx(model: ExoNS, path: str | Path, opset: int = 17) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    model.eval()
    device = next(model.parameters()).device
    state = model.initial_state(1, device)
    cfg = model.cfg
    erb = torch.zeros(1, cfg.n_erb, device=device)
    df = torch.zeros(1, cfg.df_bins, 2, device=device)
    args = (erb, df, state["h_fuse"], state["h_erb"], state["h_df"], state["h_inter_erb"], state["h_inter_df"])
    kwargs = dict(
        input_names=list(INPUTS),
        output_names=list(OUTPUTS),
        opset_version=opset,
        do_constant_folding=True,
        dynamo=False,
    )
    try:
        torch.onnx.export(FrameStep(model), args, str(path), **kwargs)
    except TypeError:
        kwargs.pop("dynamo", None)
        torch.onnx.export(FrameStep(model), args, str(path), **kwargs)
    return path


def quantize(path: str | Path, out: str | Path | None = None) -> Path:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    path = Path(path)
    out = Path(out) if out else path.with_name(path.stem + ".int8.onnx")
    quantize_dynamic(str(path), str(out), weight_type=QuantType.QInt8)
    return out


def open_session(path: str | Path, threads: int = 1):
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    return ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])


def main() -> None:
    parser = argparse.ArgumentParser(description="Export ExoNS to ONNX")
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--out", type=Path, default=Path("exons.onnx"))
    parser.add_argument("--quantize", action="store_true")
    args = parser.parse_args()
    model = load_model(args.checkpoint)
    path = export_onnx(model, args.out)
    print(path)
    if args.quantize:
        print(quantize(path))


if __name__ == "__main__":
    main()
