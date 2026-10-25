"""Manifests: one JSON line per audio file (path, sample rate, frames, channels), so mixing never scans a disk."""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

import soundfile as sf

AUDIO = {".wav", ".flac", ".ogg", ".opus", ".mp3"}


def scan(roots: list[str], exclude: str | None = None, min_seconds: float = 0.0) -> list[dict]:
    rx = re.compile(exclude) if exclude else None
    rows = []
    for root in roots:
        for p in sorted(Path(root).rglob("*")):
            if p.suffix.lower() not in AUDIO or (rx and rx.search(str(p))):
                continue
            try:
                info = sf.info(str(p))
            except RuntimeError:
                continue
            if info.frames / info.samplerate < min_seconds:
                continue
            rows.append({"path": str(p.resolve()), "sr": info.samplerate, "frames": info.frames, "channels": info.channels})
    return rows


def write(rows: list[dict], out: str) -> None:
    Path(out).parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w") as f:
        for r in rows:
            f.write(json.dumps(r) + "\n")


def read(path: str) -> list[dict]:
    with open(path) as f:
        return [json.loads(line) for line in f if line.strip()]


def hours(rows: list[dict]) -> float:
    return sum(r["frames"] / r["sr"] for r in rows) / 3600


def main() -> None:
    ap = argparse.ArgumentParser(description="Scan audio folders into a JSONL manifest.")
    ap.add_argument("out")
    ap.add_argument("roots", nargs="+")
    ap.add_argument("--exclude", help="regex of paths to leave out (e.g. a test set's noise types)")
    ap.add_argument("--min-seconds", type=float, default=0.0)
    a = ap.parse_args()
    rows = scan(a.roots, a.exclude, a.min_seconds)
    write(rows, a.out)
    print(f"{a.out}: {len(rows)} files, {hours(rows):.2f} h")


if __name__ == "__main__":
    main()
