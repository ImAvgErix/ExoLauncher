"""Scores output folders against a clean reference folder (or, with no reference, by DNSMOS alone):

    python -m exodenoise.evaluate --clean data/vb/clean_testset_wav --system noisy=data/vb/noisy_testset_wav \\
        --system dfn3=out/dfn3 --system exo=out/exo [--dnsmos] [--json results.json] [--markdown results.md]

Each system's outputs are matched to references by file name. Every output is checked for alignment (the lag of
best cross-correlation with its reference, up to 50 ms): a system that is consistently off is reported, since a
misaligned output loses SI-SDR and PESQ for reasons that have nothing to do with quality.
"""
from __future__ import annotations

import argparse
import json
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
import soundfile as sf

from . import dsp, metrics

KEYS = ["pesq", "stoi", "estoi", "si_sdr", "dnsmos_sig", "dnsmos_bak", "dnsmos_ovrl"]


def _lag(y: np.ndarray, x: np.ndarray, most: int = 2400) -> int:
    """Samples by which ``y`` trails ``x`` (negative: leads), from cross-correlation over the first 5 s."""
    n = min(len(x), len(y), 5 * dsp.SR)
    if n <= 2 * most:
        return 0
    c = np.correlate(y[:n], x[most: n - most], "valid")  # c[k]: y shifted by k - most
    return int(np.argmax(np.abs(c))) - most


_dnsmos = None


def _score(args):
    out_path, ref_path, use_dnsmos = args
    global _dnsmos
    if use_dnsmos and _dnsmos is None:
        _dnsmos = metrics.DNSMOS()
    y, sr = sf.read(out_path, dtype="float32", always_2d=True)
    y = dsp.resample(y[:, 0], sr)
    x = None
    lag = 0
    if ref_path:
        x, sr2 = sf.read(ref_path, dtype="float32", always_2d=True)
        x = dsp.resample(x[:, 0], sr2)
        lag = _lag(y, x)
    row = metrics.score(y, x, _dnsmos if use_dnsmos else None)
    row["lag_ms"] = lag / dsp.SR * 1000
    return row


def evaluate(systems: dict[str, str], clean: str | None, dnsmos: bool, limit: int | None, workers: int) -> dict:
    results = {}
    with ProcessPoolExecutor(workers) as pool:
        for name, folder in systems.items():
            files = sorted(Path(folder).glob("*.wav"))[:limit]
            jobs = [(str(f), str(Path(clean) / f.name) if clean else None, dnsmos) for f in files]
            jobs = [j for j in jobs if j[1] is None or Path(j[1]).exists()]
            rows = list(pool.map(_score, jobs, chunksize=8))
            summary = {k: float(np.nanmean([r[k] for r in rows])) for k in KEYS + ["lag_ms"] if k in rows[0]}
            summary["files"] = len(rows)
            results[name] = summary
            print(name, json.dumps({k: round(v, 3) for k, v in summary.items()}), flush=True)
    return results


def markdown(results: dict) -> str:
    keys = [k for k in KEYS if any(k in r for r in results.values())]
    head = "| system | " + " | ".join(keys) + " |\n|---|" + "---|" * len(keys) + "\n"
    rows = "".join(f"| {n} | " + " | ".join(f"{r.get(k, float('nan')):.3f}" for k in keys) + " |\n" for n, r in results.items())
    return head + rows


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--clean")
    ap.add_argument("--system", action="append", required=True, help="name=folder")
    ap.add_argument("--dnsmos", action="store_true")
    ap.add_argument("--limit", type=int)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--json")
    ap.add_argument("--markdown")
    a = ap.parse_args()
    systems = dict(s.split("=", 1) for s in a.system)
    results = evaluate(systems, a.clean, a.dnsmos, a.limit, a.workers)
    for name, r in results.items():
        if abs(r.get("lag_ms", 0)) > 1:
            print(f"warning: {name} lags its reference by {r['lag_ms']:.1f} ms on average")
    if a.json:
        json.dump(results, open(a.json, "w"), indent=1)
    if a.markdown:
        Path(a.markdown).write_text(markdown(results))
    print(markdown(results))


if __name__ == "__main__":
    main()
