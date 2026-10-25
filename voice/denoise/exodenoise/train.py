"""Training: python -m exodenoise.train configs/exo_s.yaml [--resume] [--set train.steps=1000 ...]

A run directory holds config.yaml (as run, with the git commit), metrics.jsonl (every log and validation line),
TensorBoard events when tensorboard is installed, last.pt (resumable: model, EMA, optimizer, step) and best.pt (the EMA
weights that scored best on the validation pairs). Validation runs the EMA model offline on fixed mixes (loss terms,
SI-SDR) and on a real paired test set (PESQ, STOI, SI-SDR, and DNSMOS when enabled).
"""
from __future__ import annotations

import argparse
import copy
import json
import math
import subprocess
import time
from pathlib import Path

import numpy as np
import torch
import yaml

from . import dsp, losses, metrics
from .data import mixer
from .model import ExoDenoise, ModelConfig, count_params


def load_config(path: str, overrides: list[str]) -> dict:
    cfg = yaml.safe_load(open(path))
    for o in overrides:
        key, value = o.split("=", 1)
        node = cfg
        *parents, leaf = key.split(".")
        for p in parents:
            node = node.setdefault(p, {})
        node[leaf] = yaml.safe_load(value)
    return cfg


def _tuples(d: dict) -> dict:
    return {k: tuple(v) if isinstance(v, list) else v for k, v in d.items()}


class CropPairs(torch.utils.data.Dataset):
    """Random crops of (noisy, target) pairs: real recordings with a teacher's output as the target (pseudo labels)."""

    def __init__(self, noisy_dir: str, target_dir: str, seconds: float, length: int):
        self.pairs = mixer.PairedSet(noisy_dir, target_dir)
        self.n = int(seconds * dsp.SR) // dsp.HOP * dsp.HOP
        self.length = length

    def __len__(self):
        return self.length

    def __getitem__(self, i):
        noisy, target, _ = self.pairs[int(torch.randint(len(self.pairs), (1,)))]
        if len(noisy) < self.n:
            pad = self.n - len(noisy)
            noisy, target = torch.nn.functional.pad(noisy, (0, pad)), torch.nn.functional.pad(target, (0, pad))
        at = int(torch.randint(len(noisy) - self.n + 1, (1,)))
        return noisy[at: at + self.n], target[at: at + self.n]


def _loader(ds, batch, workers, shuffle=True):
    return torch.utils.data.DataLoader(ds, batch_size=batch, shuffle=shuffle, num_workers=workers, drop_last=shuffle,
                                       persistent_workers=workers > 0, pin_memory=torch.cuda.is_available())


def _forever(loader):
    while True:
        yield from loader


def step_loss(model, noisy, clean, lcfg: losses.LossConfig):
    spec = dsp.stft(noisy)
    out, _, lsnr = model(spec)
    y = dsp.istft(out)
    true = losses.local_snr(dsp.stft(clean), spec - dsp.stft(clean), model.cfg.lsnr_min, model.cfg.lsnr_max)
    return losses.total(y, clean, lsnr, true, lcfg)


@torch.no_grad()
def validate(model, val_mix, val_pairs, lcfg, device, dnsmos=None) -> dict:
    model.eval()
    out: dict[str, list[float]] = {}
    for noisy, clean in val_mix:
        noisy, clean = noisy.to(device), clean.to(device)
        _, logs = step_loss(model, noisy, clean, lcfg)
        for k, v in logs.items():
            out.setdefault("mix_" + k, []).append(v)
    for noisy, clean, _ in val_pairs or []:
        y = model.enhance(noisy.to(device)).cpu()[0].numpy()
        for k, v in metrics.score(y, clean[0].numpy(), dnsmos).items():
            out.setdefault("pairs_" + k, []).append(v)
    model.train()
    return {k: float(np.nanmean(v)) for k, v in out.items()}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("config")
    ap.add_argument("--resume", action="store_true")
    ap.add_argument("--set", nargs="*", default=[], help="config overrides, e.g. train.steps=1000")
    a = ap.parse_args()
    cfg = load_config(a.config, a.set)
    t = cfg["train"]
    run = Path(cfg["run"])
    run.mkdir(parents=True, exist_ok=True)
    try:
        cfg["git"] = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    except OSError:
        pass
    yaml.safe_dump(cfg, open(run / "config.yaml", "w"), sort_keys=False)

    torch.manual_seed(t.get("seed", 0))
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if device.type == "cpu":
        torch.set_num_threads(t.get("threads", torch.get_num_threads()))
    model = ExoDenoise(ModelConfig(**cfg.get("model", {}))).to(device)
    ema = copy.deepcopy(model).eval()
    lcfg = losses.LossConfig(**_tuples(cfg.get("loss", {})))
    mcfg = mixer.MixConfig(**_tuples(cfg.get("mix", {})))
    d = cfg["data"]
    sources = mixer.load_sources(d["speech"], d["noise"], d.get("rir", []))
    train = mixer.Mixer(sources, mcfg, length=t["steps"] * t["batch"], seed=t.get("seed", 0))
    val_sources = mixer.load_sources(d.get("val_speech", d["speech"]), d.get("val_noise", d["noise"]), d.get("val_rir", d.get("rir", [])))
    val_mix = _loader(mixer.Mixer(val_sources, mcfg, length=d.get("val_mix", 64), seed=12345, fixed=True), t["batch"], 0, shuffle=False)
    val_pairs = None
    if "val_pairs" in d:
        vp = d["val_pairs"]
        val_pairs = _loader(mixer.PairedSet(vp["noisy"], vp["clean"], vp.get("limit")), 1, 0, shuffle=False)
    batches = _forever(_loader(train, t["batch"], t.get("workers", 4)))
    pseudo = None
    if "pseudo" in d:
        ps = d["pseudo"]
        pseudo = _forever(_loader(CropPairs(ps["noisy"], ps["target"], mcfg.seconds, 10**9), t["batch"], 1))
    dnsmos = metrics.DNSMOS() if t.get("dnsmos") else None

    opt = torch.optim.AdamW(model.parameters(), lr=t["lr"], weight_decay=t.get("weight_decay", 1e-2), betas=(0.9, 0.99))
    warmup, steps = t.get("warmup", 1000), t["steps"]
    sched = torch.optim.lr_scheduler.LambdaLR(opt, lambda s: min(1, (s + 1) / warmup) * (0.05 + 0.95 * 0.5 * (1 + math.cos(math.pi * min(1, s / steps)))))
    step, best = 0, -math.inf
    if a.resume and (run / "last.pt").exists():
        ck = torch.load(run / "last.pt", map_location=device)
        model.load_state_dict(ck["model"]); ema.load_state_dict(ck["ema"]); opt.load_state_dict(ck["opt"])
        sched.load_state_dict(ck["sched"]); step, best = ck["step"], ck["best"]
    writer = None
    try:
        from torch.utils.tensorboard import SummaryWriter

        writer = SummaryWriter(run / "tb")
    except ImportError:
        pass
    log = open(run / "metrics.jsonl", "a")

    def emit(kind: str, values: dict):
        log.write(json.dumps({"kind": kind, "step": step, "time": time.time(), **values}) + "\n")
        log.flush()
        if writer:
            for k, v in values.items():
                writer.add_scalar(f"{kind}/{k}", v, step)

    print(f"model {count_params(model) / 1e6:.2f} M params, {len(sources.speech)} speech / {len(sources.noise)} noise / {len(sources.rir)} RIR files, device {device}")
    amp = torch.autocast(device.type, dtype=torch.bfloat16, enabled=t.get("amp", False) and device.type == "cuda")
    ema_decay = t.get("ema", 0.999)
    model.train()
    started, window = time.time(), {}
    while step < steps:
        noisy, clean = next(batches)
        with amp:
            loss, logs = step_loss(model, noisy.to(device), clean.to(device), lcfg)
            if pseudo is not None and step % d["pseudo"].get("every", 4) == 0:
                pn, pt = next(pseudo)
                ploss, _ = step_loss(model, pn.to(device), pt.to(device), lcfg)
                loss = loss + d["pseudo"].get("weight", 0.5) * ploss
        opt.zero_grad(set_to_none=True)
        loss.backward()
        gnorm = torch.nn.utils.clip_grad_norm_(model.parameters(), t.get("grad_clip", 1.0))
        if not torch.isfinite(gnorm):
            print(f"step {step}: non-finite gradient, skipped")
            continue
        opt.step()
        sched.step()
        step += 1
        with torch.no_grad():
            k = min(ema_decay, (1 + step) / (10 + step))
            for pe, pm in zip(ema.parameters(), model.parameters()):
                pe.lerp_(pm, 1 - k)
            for be, bm in zip(ema.buffers(), model.buffers()):
                be.copy_(bm)
        for k_, v in logs.items():
            window.setdefault(k_, []).append(v)
        if step % t.get("log_every", 50) == 0:
            vals = {k_: float(np.mean(v)) for k_, v in window.items()}
            vals.update(lr=sched.get_last_lr()[0], grad_norm=float(gnorm), steps_per_s=t.get("log_every", 50) / (time.time() - started))
            emit("train", vals)
            print(f"step {step} " + " ".join(f"{k_}={v:.4g}" for k_, v in vals.items()), flush=True)
            started, window = time.time(), {}
        if step % t.get("val_every", 2000) == 0 or step == steps:
            vals = validate(ema, val_mix, val_pairs, lcfg, device, dnsmos)
            emit("val", vals)
            print(f"val {step} " + " ".join(f"{k_}={v:.4g}" for k_, v in vals.items()), flush=True)
            score = vals.get("pairs_pesq", vals.get("mix_sisdr", -math.inf))
            state = {"model": model.state_dict(), "ema": ema.state_dict(), "opt": opt.state_dict(), "sched": sched.state_dict(),
                     "step": step, "best": max(best, score), "config": cfg}
            torch.save(state, run / "last.pt")
            if score > best:
                best = score
                torch.save({"model": ema.state_dict(), "model_config": ema.config_dict(), "step": step, "val": vals}, run / "best.pt")


if __name__ == "__main__":
    main()
