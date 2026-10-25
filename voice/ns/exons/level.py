"""Input leveler, the same rule Exo's voice chain uses in front of DeepFilterNet.

A model trained around one level cleans a quiet mic and a hot mic differently.
Krisp's published integration (and Exo's measurement of it) brings the running
level to about -24 dBFS, lifts at most 40 dB, and cuts at most 20 dB, and only
while the previous frame was judged to be voice. The gain is undone on the way
out, so the talker's own level is what the call sends.
"""

from __future__ import annotations

import math

import numpy as np

MODEL_DB = -24.0
LEVEL_FROM_DB = -70.0
MOST_LIFT_DB = 40.0
MOST_CUT_DB = 20.0
LEVEL_SECONDS = 3.0
SPEECH_OPEN_DB = 10.0


def frame_db(mono: np.ndarray) -> float:
    power = float(np.mean(np.square(np.asarray(mono, dtype=np.float64))))
    return 10.0 * math.log10(power + 1e-12)


def update_level(
    level_db: float | None,
    current_db: float,
    speech_db_prev: float | None,
    n_samples: int,
    sr: int,
    level_seconds: float = LEVEL_SECONDS,
    level_from: float = LEVEL_FROM_DB,
    speech_open: float = SPEECH_OPEN_DB,
) -> float | None:
    if current_db > level_from and speech_db_prev is not None and speech_db_prev > speech_open:
        if level_db is None:
            return current_db
        return level_db + (current_db - level_db) * (n_samples / (level_seconds * sr))
    return level_db


def level_gain(
    level_db: float | None,
    model_db: float = MODEL_DB,
    most_cut: float = MOST_CUT_DB,
    most_lift: float = MOST_LIFT_DB,
) -> float:
    if level_db is None:
        return 1.0
    delta = min(most_lift, max(-most_cut, model_db - level_db))
    return 10.0 ** (delta / 20.0)
