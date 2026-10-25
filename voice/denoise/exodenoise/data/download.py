"""Public data, fetched and unpacked: python -m exodenoise.data.download <root> <name> [<name> ...]

Then build manifests with ``python -m exodenoise.data.manifest`` (``prepare.sh`` does both for the default recipe).
Sizes are the downloads; unpacked is about 1.5x. Every entry is a public corpus with its license on its page; check it
fits your use before training a model you ship (VCTK/VoiceBank: CC BY 4.0; DEMAND: CC BY-SA 3.0; DNS Challenge: per
source, mostly CC BY / CC0; EARS: CC BY-NC 4.0, so research only; OpenSLR 28: Apache 2.0).
"""
from __future__ import annotations

import argparse
import subprocess
from pathlib import Path

VB = "https://datashare.ed.ac.uk/bitstream/handle/10283/2791/"
DEMAND = "https://zenodo.org/records/1227121/files/"
DNS5 = "https://dnschallengepublic.blob.core.windows.net/dns5archive/V5_training_dataset/"
# VoiceBank-DEMAND's test set mixes these five DEMAND noises: they stay out of training so the test stays unseen.
DEMAND_TEST = ["DLIVING", "OOFFICE", "TBUS", "SCAFE", "SPSQUARE"]
DEMAND_TRAIN = ["DKITCHEN", "DWASHING", "NFIELD", "NPARK", "NRIVER", "OHALLWAY", "OMEETING", "PCAFETER", "PRESTO",
                "PSTATION", "STRAFFIC", "TCAR", "TMETRO"]

SETS: dict[str, list[str]] = {
    # 48 kHz read speech, 28 speakers (2.5 GB) and the standard test set (11 + 0.3 GB with its noisy pair).
    "vb_train": [VB + "clean_trainset_28spk_wav.zip"],
    "vb_test": [VB + "clean_testset_wav.zip", VB + "noisy_testset_wav.zip"],
    # 48 kHz 16-channel environmental noise; 13 types for training (3.9 GB).
    "demand": [DEMAND + f"{n}_48k.zip" for n in DEMAND_TRAIN],
    # DNS Challenge 5 (fullband, 48 kHz): measured and simulated RIRs (0.3 GB), emotional speech (39 MB),
    # Freesound noise (3.5 GB, keyboards, fans, clicks, appliances, crowds) and AudioSet noise (7 x 5.4 GB).
    "dns_rir": [DNS5 + "datasets_fullband.impulse_responses_000.tar.bz2"],
    "dns_emotional": [DNS5 + "Track1_Headset/emotional_speech.tgz"],
    "dns_freesound": [DNS5 + f"noise_fullband/datasets_fullband.noise_fullband.freesound_00{i}.tar.bz2" for i in range(2)],
    "dns_audioset": [DNS5 + f"noise_fullband/datasets_fullband.noise_fullband.audioset_00{i}.tar.bz2" for i in range(7)],
    "dns_vctk": [DNS5 + "Track1_Headset/vctk_wav48_silence_trimmed.tgz"],
    # DNS read speech (LibriVox, 299 GB unpacked) is split in many parts: use Microsoft's own script,
    # download-dns-challenge-5-headset-training.sh, and point the speech manifest at datasets_fullband/clean_fullband.
    # Simulated and real RIRs at 16 kHz (1.3 GB): more rooms, band-limited tails.
    "openslr_rirs": ["https://www.openslr.org/resources/28/rirs_noises.zip"],
    # LibriTTS-R (24 kHz, restored LibriTTS): many speakers, kept band-limited by the mixer (train-clean-100: 8 GB).
    "libritts_r": ["https://www.openslr.org/resources/141/train_clean_100.tar.gz"],
    # MUSAN noise and music (11 GB, 16 kHz).
    "musan": ["https://www.openslr.org/resources/17/musan.tar.gz"],
    # EARS: 100 h of expressive anechoic 48 kHz speech (emotions, whisper, shouting, nonverbal), CC BY-NC.
    "ears": [f"https://github.com/facebookresearch/ears_dataset/releases/download/dataset/p{i:03d}.zip" for i in range(1, 108)],
}


def fetch(url: str, root: Path) -> None:
    name = url.rsplit("/", 1)[1]
    done = root / f".{name}.done"
    if done.exists():
        return
    print("fetching", url, flush=True)
    if name.endswith((".tar.bz2", ".tgz", ".tar.gz")):
        flag = "j" if name.endswith("bz2") else "z"
        subprocess.run(f"curl -fsSL --retry 4 '{url}' | tar -x{flag}f - -C '{root}'", shell=True, check=True)
    else:
        part = root / (name + ".part")
        subprocess.run(["curl", "-fsSL", "--retry", "4", "-C", "-", "-o", str(part), url], check=True)
        if name.endswith(".zip"):
            subprocess.run(["unzip", "-q", "-o", str(part), "-d", str(root / Path(name).stem)], check=True)
            part.unlink()
        else:
            part.rename(root / name)
    done.touch()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("names", nargs="+", choices=sorted(SETS))
    a = ap.parse_args()
    root = Path(a.root)
    root.mkdir(parents=True, exist_ok=True)
    for name in a.names:
        (root / name).mkdir(exist_ok=True)
        for url in SETS[name]:
            fetch(url, root / name)


if __name__ == "__main__":
    main()
