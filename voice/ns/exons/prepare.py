"""Index a directory of wavs into a training manifest.

Expected layout, or pass the three directories yourself::

    data/speech/**/*.wav
    data/noise/**/*.wav
    data/rir/**/*.wav

Anything soundfile (or the stdlib wav reader) can open is accepted. Files are
not copied and not resampled here; the mixer resamples when it loads them.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

AUDIO = {".wav", ".flac", ".ogg", ".aiff", ".aif"}


def scan(root: Path | None) -> list[str]:
    if root is None or not root.exists():
        return []
    return sorted(str(path) for path in root.rglob("*") if path.suffix.lower() in AUDIO)


def main() -> None:
    parser = argparse.ArgumentParser(description="Write a speech/noise/rir manifest")
    parser.add_argument("--speech", type=Path, default=None)
    parser.add_argument("--noise", type=Path, default=None)
    parser.add_argument("--rir", type=Path, default=None)
    parser.add_argument("--out", type=Path, default=Path("manifest.json"))
    args = parser.parse_args()
    manifest = {"speech": scan(args.speech), "noise": scan(args.noise), "rir": scan(args.rir)}
    args.out.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"{args.out}: {len(manifest['speech'])} speech, {len(manifest['noise'])} noise, {len(manifest['rir'])} rirs")


if __name__ == "__main__":
    main()
