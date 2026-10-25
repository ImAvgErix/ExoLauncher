"""Objective metrics: SI-SDR, PESQ (wide band, at 16 kHz, ITU-T P.862.2), STOI and extended STOI (at 10 kHz inside
pystoi), and DNSMOS P.835 (SIG, BAK, OVRL; Microsoft's non-intrusive MOS predictor, at 16 kHz), which needs no
clean reference and so also scores real recordings."""
from __future__ import annotations

import math
import urllib.request
from pathlib import Path

import numpy as np

from . import dsp

DNSMOS_URL = "https://raw.githubusercontent.com/microsoft/DNS-Challenge/master/DNSMOS/DNSMOS/sig_bak_ovr.onnx"


def si_sdr(y: np.ndarray, x: np.ndarray) -> float:
    x = x - x.mean()
    y = y - y.mean()
    proj = np.dot(y, x) / (np.dot(x, x) + 1e-12) * x
    return float(10 * np.log10(np.dot(proj, proj) / (np.dot(y - proj, y - proj) + 1e-12) + 1e-12))


def pesq_wb(y: np.ndarray, x: np.ndarray, sr: int = dsp.SR) -> float:
    from pesq import pesq

    try:
        return float(pesq(16000, dsp.resample(x, sr, 16000), dsp.resample(y, sr, 16000), "wb"))
    except Exception:  # no utterance detected, etc.
        return math.nan


def stoi(y: np.ndarray, x: np.ndarray, sr: int = dsp.SR, extended: bool = False) -> float:
    from pystoi import stoi as _stoi

    return float(_stoi(x, y, sr, extended=extended))


class DNSMOS:
    """DNSMOS P.835 on 9.01 s windows hopped by 1 s, averaged, with Microsoft's polynomial calibration
    (the personalized=False path of DNSMOS/dnsmos_local.py)."""

    WINDOW = 144160  # 9.01 s at 16 kHz

    def __init__(self, cache: str = "~/.cache/exodenoise"):
        import onnxruntime as ort

        path = Path(cache).expanduser() / "dnsmos_sig_bak_ovr.onnx"
        if not path.exists():
            path.parent.mkdir(parents=True, exist_ok=True)
            urllib.request.urlretrieve(DNSMOS_URL, path)
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 1
        self.sess = ort.InferenceSession(str(path), opts, providers=["CPUExecutionProvider"])
        self.input = self.sess.get_inputs()[0].name

    @staticmethod
    def _poly(sig, bak, ovr):
        p_ovr = np.poly1d([-0.06766283, 1.11546468, 0.04602535])
        p_sig = np.poly1d([-0.08397278, 1.22083953, 0.0052439])
        p_bak = np.poly1d([-0.13166888, 1.60915514, -0.39604546])
        return p_sig(sig), p_bak(bak), p_ovr(ovr)

    def __call__(self, y: np.ndarray, sr: int = dsp.SR) -> dict:
        x = dsp.resample(y.astype(np.float32), sr, 16000)
        while len(x) < self.WINDOW:
            x = np.concatenate([x, x])
        scores = []
        n_windows = int(np.floor(len(x) / 16000) - self.WINDOW / 16000) + 1
        for i in range(max(1, n_windows)):
            seg = x[i * 16000: i * 16000 + self.WINDOW]
            if len(seg) < self.WINDOW:
                break
            sig, bak, ovr = self.sess.run(None, {self.input: seg[None].astype(np.float32)})[0][0]
            scores.append(self._poly(sig, bak, ovr))
        sig, bak, ovr = np.mean(scores, 0)
        return {"dnsmos_sig": float(sig), "dnsmos_bak": float(bak), "dnsmos_ovrl": float(ovr)}


def score(y: np.ndarray, x: np.ndarray | None, dnsmos: DNSMOS | None = None, sr: int = dsp.SR) -> dict:
    """Every metric for one utterance; reference-free ones only when ``x`` is None (a real recording)."""
    out = {}
    if x is not None:
        n = min(len(y), len(x))
        y, x = y[:n], x[:n]
        out.update(si_sdr=si_sdr(y, x), pesq=pesq_wb(y, x, sr), stoi=stoi(y, x, sr), estoi=stoi(y, x, sr, True))
    if dnsmos is not None:
        out.update(dnsmos(y, sr))
    return out
