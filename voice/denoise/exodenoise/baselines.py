"""Baselines, each run over a folder of noisy wavs into a folder of 48 kHz outputs aligned with the input:

    python -m exodenoise.baselines <name> <noisy_dir> <out_dir>

Kept apart from training (its own environment: ``requirements-baselines.txt``, Python 3.11, since DeepFilterNet's
package pins an older torchaudio) and scored by ``exodenoise.evaluate`` like any other output folder.

    dfn3       DeepFilterNet3 (pip deepfilternet 0.5.6), offline, 2 frames of lookahead: 40 ms algorithmic latency
    dfn3_ll    DeepFilterNet3's low-latency model (no lookahead, 20 ms), the one Exo ships today, run from its ONNX
               export with DeepFilterNet's own feature code; ``--atten`` sets the limit (Exo's default is 70 dB)
    rnnoise    RNNoise (pyrnnoise 0.4.5's bundled librnnoise), 48 kHz, 10 ms frames
    demucs     Facebook Denoiser, the dns64 model (16 kHz; offline, so no streaming penalty)
    dtln       DTLN (breizhn/DTLN's ONNX pair, 16 kHz, 32 ms blocks, 8 ms shift), run block by block
    cmd:TPL    any command line, run once per file: TPL holds {in} and {out}. NVIDIA Maxine's sample app
               (effects_demo with a denoiser config), a Krisp SDK sample, or any licensed SDK's CLI.
    exo:PATH   an exported Exo Denoise ONNX model, streamed hop by hop exactly as it runs live

Closed SDKs are not bundled and their outputs are never used as training targets here: the hook only lets someone
holding a license score them on the same files. A folder of outputs produced elsewhere (a Krisp recording through
Discord's own mic test, for instance) is scored directly by ``evaluate`` with no baseline run at all.
"""
from __future__ import annotations

import argparse
import shlex
import subprocess
import sys
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

CACHE = Path("~/.cache/exodenoise").expanduser()
DFN_MODELS = "https://raw.githubusercontent.com/Rikorose/DeepFilterNet/main/models/"
DTLN_MODELS = "https://raw.githubusercontent.com/breizhn/DTLN/master/pretrained_model/"


def _fetch(url: str, name: str) -> Path:
    path = CACHE / name
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        urllib.request.urlretrieve(url, path)
    return path


def _resample(x: np.ndarray, sr_in: int, sr_out: int) -> np.ndarray:
    from math import gcd

    from scipy.signal import resample_poly

    if sr_in == sr_out:
        return x.astype(np.float32)
    g = gcd(sr_in, sr_out)
    return resample_poly(x, sr_out // g, sr_in // g).astype(np.float32)


def dfn3(atten: float | None):
    from df.enhance import enhance, init_df
    import torch

    import zipfile

    folder = CACHE / "DeepFilterNet3"
    if not (folder / "config.ini").exists():  # DeepFilterNet's own fetch goes to github.com, a raw URL is the same file
        with zipfile.ZipFile(_fetch(DFN_MODELS + "DeepFilterNet3.zip", "DeepFilterNet3.zip")) as z:
            z.extractall(CACHE)
    model, state, _ = init_df(model_base_dir=str(folder), post_filter=False, log_level="ERROR")

    def run(x: np.ndarray) -> np.ndarray:
        return enhance(model, state, torch.from_numpy(x)[None], atten_lim_db=atten)[0].numpy()
    return run


def dfn3_ll(atten: float | None, folder: Path | None = None):
    """The ONNX graphs of DeepFilterNet3_ll with DeepFilterNet's own STFT, features, mask and deep filter.
    ``folder`` holds enc/erb_dec/df_dec.onnx and config.ini (a fine-tuned export); default: the published model."""
    import tarfile

    import onnxruntime as ort
    import torch
    from df.config import config
    from df.enhance import df_features
    from df.modules import Mask, erb_fb
    from df.multiframe import DF as DeepFilter
    from libdf import DF

    published = folder is None
    folder = folder or CACHE / "dfn3_ll"
    if published and not (folder / "enc.onnx").exists():
        with tarfile.open(_fetch(DFN_MODELS + "DeepFilterNet3_ll_onnx.tar.gz", "DeepFilterNet3_ll_onnx.tar.gz")) as t:
            for m in t.getmembers():
                if m.isfile() and Path(m.name).suffix in (".onnx", ".ini"):
                    m.name = Path(m.name).name
                    t.extract(m, folder)
    config.load(str(folder / "config.ini"), allow_reload=True)  # df_features reads its norm time constant from here
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 1
    enc, erb_dec, df_dec = (ort.InferenceSession(str(folder / f"{n}.onnx"), opts) for n in ("enc", "erb_dec", "df_dec"))
    state = DF(sr=48000, fft_size=960, hop_size=480, nb_bands=32, min_nb_erb_freqs=2)
    mask = Mask(erb_fb(state.erb_widths(), 48000, inverse=True))
    deep_filter = DeepFilter(num_freqs=96, frame_size=5, lookahead=0)

    def run(x: np.ndarray) -> np.ndarray:
        n = len(x)
        audio = torch.from_numpy(np.pad(x, (0, 960)))[None]
        spec, feat_erb, feat_spec = df_features(audio, state, 96)
        feat_spec = feat_spec.squeeze(1).permute(0, 3, 1, 2)
        e0, e1, e2, e3, emb, c0, _ = enc.run(None, {"feat_erb": feat_erb.numpy(), "feat_spec": feat_spec.numpy()})
        m = erb_dec.run(None, {"emb": emb, "e3": e3, "e2": e2, "e1": e1, "e0": e0})[0]
        coefs = df_dec.run(None, {"emb": emb, "c0": c0})[0]
        with torch.no_grad():
            coefs = torch.from_numpy(coefs).unflatten(-1, (-1, 2)).permute(0, 3, 1, 2, 4)
            spec_m = mask(spec, torch.from_numpy(m))
            spec_e = deep_filter(spec.clone(), coefs)
            spec_e[..., 96:, :] = spec_m[..., 96:, :]
            out = torch.view_as_complex(spec_e.squeeze(1).contiguous())
            if atten:
                lim = 10 ** (-atten / 20)
                out = out * (1 - lim) + torch.view_as_complex(spec.squeeze(1).contiguous()) * lim
        y = state.synthesis(out.numpy())[0]
        return y[480: 480 + n]
    return run


def rnnoise():
    """librnnoise frame by frame (its C API through pyrnnoise), the output moved back by its delay: two 10 ms frames,
    measured (cross-correlation with the clean reference peaks at 960 samples on every file)."""
    from pyrnnoise import rnnoise as rn

    def run(x: np.ndarray) -> np.ndarray:
        state = rn.create()
        pcm = np.pad(np.clip(x, -1, 1) * 32767, (0, 3 * rn.FRAME_SIZE)).astype(np.int16)
        out = [rn.process_mono_frame(state, pcm[i: i + rn.FRAME_SIZE])[0] for i in range(0, len(pcm) - rn.FRAME_SIZE + 1, rn.FRAME_SIZE)]
        rn.destroy(state)
        y = np.concatenate(out).astype(np.float32) / 32767
        return y[2 * rn.FRAME_SIZE: 2 * rn.FRAME_SIZE + len(x)]
    return run


def demucs():
    import torch
    from denoiser import pretrained

    model = pretrained.dns64().eval()

    def run(x: np.ndarray) -> np.ndarray:
        with torch.no_grad():
            y = model(torch.from_numpy(_resample(x, 48000, 16000))[None, None])[0, 0].numpy()
        return _resample(y, 16000, 48000)[: len(x)]
    return run


def dtln():
    import onnxruntime as ort

    s1 = ort.InferenceSession(str(_fetch(DTLN_MODELS + "model_1.onnx", "dtln_1.onnx")))
    s2 = ort.InferenceSession(str(_fetch(DTLN_MODELS + "model_2.onnx", "dtln_2.onnx")))
    block, shift = 512, 128

    def zeros(sess):
        return {i.name: np.zeros([d if isinstance(d, int) else 1 for d in i.shape], np.float32) for i in sess.get_inputs()}

    def run(x: np.ndarray) -> np.ndarray:
        audio = _resample(x, 48000, 16000)
        n = len(audio)
        audio = np.pad(audio, (block - shift, block))
        in1, in2 = zeros(s1), zeros(s2)
        names1, names2 = [i.name for i in s1.get_inputs()], [i.name for i in s2.get_inputs()]
        in_buf, out_buf = np.zeros(block, np.float32), np.zeros(block, np.float32)
        out = np.zeros(len(audio), np.float32)
        for i in range((len(audio) - block) // shift):
            in_buf[:-shift] = in_buf[shift:]
            in_buf[-shift:] = audio[i * shift: i * shift + shift]
            spec = np.fft.rfft(in_buf)
            in1[names1[0]] = np.abs(spec).reshape(1, 1, -1).astype(np.float32)
            mask, in1[names1[1]] = s1.run(None, in1)
            est = np.fft.irfft(np.abs(spec) * mask.reshape(-1) * np.exp(1j * np.angle(spec))).astype(np.float32)
            in2[names2[0]] = est.reshape(1, 1, -1)
            blk, in2[names2[1]] = s2.run(None, in2)
            out_buf[:-shift] = out_buf[shift:]
            out_buf[-shift:] = 0
            out_buf += blk.reshape(-1)
            out[i * shift: i * shift + shift] = out_buf[:shift]
        # The output lags its input by one block (padding included): cut it back into place.
        y = out[block - shift + (block - shift): block - shift + (block - shift) + n]
        return _resample(np.pad(y, (0, n - len(y))), 16000, 48000)[: len(x)]
    return run


def exo(path: str, atten: float | None):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from exodenoise.stream import StreamingDenoiser

    den = StreamingDenoiser(path, atten_lim_db=atten if atten else 100.0)

    def run(x: np.ndarray) -> np.ndarray:
        den.reset()
        return den.process_file(x)
    return run


def command(template: str):
    def run_file(src: Path, dst: Path) -> None:
        subprocess.run(shlex.split(template.format(**{"in": shlex.quote(str(src)), "out": shlex.quote(str(dst))})), check=True)
    return run_file


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("name")
    ap.add_argument("noisy_dir")
    ap.add_argument("out_dir")
    ap.add_argument("--atten", type=float, default=None, help="attenuation limit in dB (dfn3, dfn3_ll, exo)")
    ap.add_argument("--limit", type=int)
    ap.add_argument("--onnx-dir", help="dfn3_ll: a folder of fine-tuned graphs (export.py's tmp/export) instead of the published ones")
    a = ap.parse_args()
    files = sorted(Path(a.noisy_dir).glob("*.wav"))[: a.limit]
    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    if a.name.startswith("cmd:"):
        run_file = command(a.name[4:])
        for f in files:
            if not (out / f.name).exists():
                run_file(f, out / f.name)
        return
    run = {
        "dfn3": lambda: dfn3(a.atten), "dfn3_ll": lambda: dfn3_ll(a.atten, Path(a.onnx_dir) if a.onnx_dir else None), "rnnoise": rnnoise,
        "demucs": demucs, "dtln": dtln,
    }.get(a.name, lambda: exo(a.name[4:], a.atten) if a.name.startswith("exo:") else sys.exit(f"unknown baseline {a.name}"))()
    for i, f in enumerate(files):
        if (out / f.name).exists():  # resumes where a stopped run left off
            continue
        x, sr = sf.read(str(f), dtype="float32", always_2d=True)
        x = _resample(x[:, 0], sr, 48000)
        y = np.asarray(run(x), np.float32)
        sf.write(str(out / f.name), y, 48000, subtype="FLOAT")
        if (i + 1) % 100 == 0:
            print(f"{a.name}: {i + 1}/{len(files)}", flush=True)


if __name__ == "__main__":
    main()
