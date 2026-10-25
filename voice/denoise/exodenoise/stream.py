"""The exported model in a streaming runtime: hops in, cleaned hops out, state kept here. No PyTorch needed."""
from __future__ import annotations

import numpy as np


class StreamingDenoiser:
    """``process(hop)`` takes 480 float32 samples at 48 kHz and returns the hop before it, cleaned; ``lsnr`` is the
    model's local SNR for it (dB, -15 noise alone to 35 clean voice), what a voice gate can listen to."""

    def __init__(self, model: str, atten_lim_db: float = 100.0, threads: int = 1, providers: list[str] | None = None):
        import onnxruntime as ort

        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads
        opts.inter_op_num_threads = 1
        opts.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.add_session_config_entry("session.intra_op.allow_spinning", "0")  # no busy-waiting between hops
        self.sess = ort.InferenceSession(str(model), opts, providers=providers or ["CPUExecutionProvider"])
        meta = self.sess.get_modelmeta().custom_metadata_map
        self.hop = int(meta["hop"])
        self.initial = np.array(meta["initial_state"].split(), np.float32)[None]
        self.atten = np.array([atten_lim_db], np.float32)
        self.reset()

    def reset(self) -> None:
        self.state = self.initial.copy()
        self.lsnr = float("nan")

    def process(self, hop: np.ndarray) -> np.ndarray:
        out, self.state, lsnr = self.sess.run(None, {"hop": hop.reshape(1, self.hop).astype(np.float32, copy=False),
                                                     "state": self.state, "atten_lim_db": self.atten})
        self.lsnr = float(lsnr[0])
        return out[0]

    def process_file(self, x: np.ndarray) -> np.ndarray:
        """A whole signal through the stream, delay removed: same length out as in."""
        n = len(x)
        x = np.pad(x.astype(np.float32), (0, (-n) % self.hop + self.hop))
        y = np.concatenate([self.process(x[i: i + self.hop]) for i in range(0, len(x), self.hop)])
        return y[self.hop: self.hop + n]
