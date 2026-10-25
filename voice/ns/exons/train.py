"""Train ExoNS.

A run writes ``config.json``, ``metrics.jsonl``, ``last.pt`` and ``best.pt``
under the output directory. ``best.pt`` is the weights with the highest
validation SI-SDR improvement. Resume with ``--resume``.
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np
import torch

from exons.config import Config, load_preset
from exons.data import MixDataset, collate, load_manifest
from exons.losses import compute_losses, si_sdr


def _speech_sisdr(estimate: torch.Tensor, reference: torch.Tensor) -> torch.Tensor:
    scores = si_sdr(estimate, reference)
    valid = reference.square().mean(dim=-1) > 1e-6
    return scores[valid] if bool(valid.any()) else scores[:0]
from exons.model import ExoNS, count_parameters


def training_step(model: ExoNS, batch: dict[str, torch.Tensor], optimizer: torch.optim.Optimizer) -> dict[str, float]:
    model.train()
    enhanced, pred_snr = model(batch["noisy"])
    parts = compute_losses(
        enhanced,
        batch["clean"],
        pred_snr,
        batch["speech"],
        batch["noise"],
        model.stft,
        model.cfg,
    )
    optimizer.zero_grad(set_to_none=True)
    parts["loss"].backward()
    grad = torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
    optimizer.step()
    out = {key: float(value.detach()) for key, value in parts.items()}
    out["grad"] = float(grad)
    with torch.no_grad():
        spoken = _speech_sisdr(enhanced, batch["clean"])
        out["sisdr_db"] = float(spoken.mean()) if spoken.numel() else float("nan")
    return out


@torch.no_grad()
def evaluate(model: ExoNS, loader: torch.utils.data.DataLoader, device: torch.device) -> dict[str, float]:
    model.eval()
    enhanced_scores = []
    noisy_scores = []
    for batch in loader:
        batch = {key: value.to(device) for key, value in batch.items()}
        enhanced, _snr = model(batch["noisy"])
        enhanced_scores.append(_speech_sisdr(enhanced, batch["clean"]))
        noisy_scores.append(_speech_sisdr(batch["noisy"], batch["clean"]))
    enhanced_cat = torch.cat([item for item in enhanced_scores if item.numel()])
    noisy_cat = torch.cat([item for item in noisy_scores if item.numel()])
    enhanced_s = enhanced_cat.mean()
    noisy_s = noisy_cat.mean()
    return {
        "val_sisdr": float(enhanced_s),
        "val_sisdr_noisy": float(noisy_s),
        "val_sisdri": float(enhanced_s - noisy_s),
    }


def save_checkpoint(path: Path, model: ExoNS, step: int, val: dict[str, float], optimizer=None) -> None:
    payload = {"config": model.cfg.to_dict(), "model": model.state_dict(), "step": step, "val": val}
    if optimizer is not None:
        payload["optimizer"] = optimizer.state_dict()
    path.parent.mkdir(parents=True, exist_ok=True)
    torch.save(payload, path)


def load_model(path: str | Path, device: str | torch.device = "cpu") -> ExoNS:
    checkpoint = torch.load(path, map_location=device, weights_only=False)
    model = ExoNS(Config.from_dict(checkpoint["config"]))
    model.load_state_dict(checkpoint["model"])
    model.to(device)
    model.eval()
    return model


def run(
    config: Config,
    out_dir: Path,
    steps: int,
    batch_size: int,
    segment: float,
    seed: int = 0,
    lr: float = 1e-3,
    device: str = "cpu",
    manifest: str | None = None,
    val_every: int = 50,
    log_every: int = 10,
    resume: str | None = None,
    overfit: bool = False,
    train_items: int = 64,
    val_items: int = 8,
) -> dict[str, float]:
    torch.manual_seed(seed)
    np.random.seed(seed)
    device_t = torch.device(device)
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "config.json").write_text(json.dumps(config.to_dict(), indent=2), encoding="utf-8")
    files = load_manifest(manifest)
    model = ExoNS(config).to(device_t)
    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-6)
    start = 1
    if resume:
        checkpoint = torch.load(resume, map_location=device_t, weights_only=False)
        model.load_state_dict(checkpoint["model"])
        if "optimizer" in checkpoint:
            optimizer.load_state_dict(checkpoint["optimizer"])
        start = int(checkpoint.get("step", 0)) + 1
    train_set = MixDataset(config, n=max(batch_size, train_items), segment=segment, seed=seed, manifest=files, fixed=overfit)
    val_set = MixDataset(config, n=val_items, segment=segment, seed=seed + 10_000, manifest=files, fixed=True)
    train_loader = torch.utils.data.DataLoader(train_set, batch_size=batch_size, shuffle=not overfit, collate_fn=collate, drop_last=True)
    val_loader = torch.utils.data.DataLoader(val_set, batch_size=batch_size, shuffle=False, collate_fn=collate)
    log_path = out_dir / "metrics.jsonl"
    best = -1e9
    last_val: dict[str, float] = {}
    step = start - 1
    started = time.perf_counter()
    while step < steps:
        train_set.epoch = step // max(1, len(train_loader))
        for batch in train_loader:
            step += 1
            if step > steps:
                break
            # Cosine from `lr` down to 1e-5. Short runs still move.
            scale = 0.5 * (1 + np.cos(np.pi * (step - 1) / max(1, steps)))
            for group in optimizer.param_groups:
                group["lr"] = 1e-5 + (lr - 1e-5) * scale
            batch = {key: value.to(device_t) for key, value in batch.items()}
            stats = training_step(model, batch, optimizer)
            if step % log_every == 0 or step == 1:
                row = {"step": step, "lr": optimizer.param_groups[0]["lr"], **stats}
                with log_path.open("a", encoding="utf-8") as handle:
                    handle.write(json.dumps(row) + "\n")
            if step % val_every == 0 or step == steps:
                last_val = evaluate(model, val_loader, device_t)
                last_val["step"] = step
                with log_path.open("a", encoding="utf-8") as handle:
                    handle.write(json.dumps(last_val) + "\n")
                save_checkpoint(out_dir / "last.pt", model, step, last_val, optimizer)
                if last_val["val_sisdri"] > best:
                    best = last_val["val_sisdri"]
                    save_checkpoint(out_dir / "best.pt", model, step, last_val)
    elapsed = time.perf_counter() - started
    summary = {
        "steps": step,
        "parameters": count_parameters(model),
        "seconds": elapsed,
        "best_sisdri": best,
        **last_val,
    }
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def main() -> None:
    parser = argparse.ArgumentParser(description="Train the ExoNS noise suppressor")
    parser.add_argument("--preset", default="realtime", choices=["realtime", "quality", "tiny"])
    parser.add_argument("--out", type=Path, default=Path("runs/exons"))
    parser.add_argument("--steps", type=int, default=20_000)
    parser.add_argument("--batch", type=int, default=4)
    parser.add_argument("--segment", type=float, default=3.0, help="seconds of audio per example")
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--manifest", default=None, help="JSON with speech, noise and rir file lists")
    parser.add_argument("--val-every", type=int, default=200)
    parser.add_argument("--resume", default=None)
    parser.add_argument("--overfit", action="store_true", help="repeat one batch, to prove the loss can fall")
    args = parser.parse_args()
    summary = run(
        load_preset(args.preset),
        args.out,
        steps=args.steps,
        batch_size=args.batch,
        segment=args.segment,
        seed=args.seed,
        lr=args.lr,
        device=args.device,
        manifest=args.manifest,
        val_every=args.val_every,
        resume=args.resume,
        overfit=args.overfit,
    )
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
