"""What could fail silently:
- mixes at the wrong SNR or level (a gain applied to one side only): the model trains on the wrong problem;
- a noise-only mix whose target isn't silence, or a clean-only mix whose target isn't the input;
- validation mixes that change between runs (scores not comparable);
- losses that reward the wrong thing: zero for a perfect output, larger for over-suppression than its mirror.
"""
import numpy as np
import soundfile as sf
import torch

from exodenoise import dsp, losses
from exodenoise.data import manifest, mixer


def _corpus(tmp_path):
    rng = np.random.default_rng(0)
    for i in range(3):
        t = np.arange(48000 * 2) / 48000
        sf.write(tmp_path / f"s{i}.wav", (0.3 * np.sin(2 * np.pi * (150 + 50 * i) * t)).astype(np.float32), 48000)
        sf.write(tmp_path / f"n{i}.wav", (0.1 * rng.standard_normal(16000)).astype(np.float32), 16000)
    rir = np.zeros(4800, np.float32)
    rir[100] = 1
    rir[2000] = 0.3
    sf.write(tmp_path / "r.wav", rir, 48000)
    rows = manifest.scan([str(tmp_path)])
    speech = [r for r in rows if "/s" in r["path"]]
    noise = [r for r in rows if "/n" in r["path"]]
    rirs = [r for r in rows if r["path"].endswith("r.wav")]
    return mixer.Sources(speech, noise, rirs)


def test_snr_and_levels(tmp_path):
    src = _corpus(tmp_path)
    cfg = mixer.MixConfig(snr_db=(10, 10), p_rir=0, p_babble=0, p_noise_only=0, p_clean_only=0, p_eq=0, p_clip=0,
                          p_lowpass_speech=0, level_dbfs=(-20, -20))
    noisy, clean = mixer.Mixer(src, cfg, 4, fixed=True).mix(np.random.default_rng(3))
    snr = 10 * np.log10(np.sum(clean ** 2) / np.sum((noisy - clean) ** 2))
    assert abs(snr - 10) < 0.1
    assert abs(10 * np.log10(np.mean(noisy ** 2)) + 20) < 0.1
    assert len(noisy) % dsp.HOP == 0


def test_noise_only_and_clean_only(tmp_path):
    src = _corpus(tmp_path)
    m = mixer.Mixer(src, mixer.MixConfig(p_noise_only=1, p_clean_only=0, p_clip=0), 1)
    noisy, clean = m.mix(np.random.default_rng(0))
    assert np.all(clean == 0) and np.abs(noisy).max() > 0
    m = mixer.Mixer(src, mixer.MixConfig(p_noise_only=0, p_clean_only=1, p_rir=0, p_clip=0), 1)
    noisy, clean = m.mix(np.random.default_rng(0))
    assert np.allclose(noisy, clean)


def test_validation_mixes_are_fixed(tmp_path):
    src = _corpus(tmp_path)
    m = mixer.Mixer(src, mixer.MixConfig(), 4, seed=7, fixed=True)
    assert torch.equal(m[2][0], m[2][0])
    assert not torch.equal(m[1][0], m[2][0])


def test_losses_order():
    torch.manual_seed(0)
    clean = torch.randn(2, 9600) * 0.1
    cfg = losses.LossConfig()
    zero, _ = losses.spectral_losses(clean, clean, cfg)
    assert zero.item() < 1e-6
    _, under = losses.spectral_losses(clean * 0.5, clean, cfg)
    _, over = losses.spectral_losses(clean * 1.5, clean, cfg)
    assert under > over  # quieter than the speech costs more than louder
    assert losses.si_sdr(clean, clean).min() > 60
