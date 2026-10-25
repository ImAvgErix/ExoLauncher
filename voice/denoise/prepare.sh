#!/usr/bin/env bash
# Fetches the small recipe's data (configs/vb_demand.yaml, about 8 GB) into data/ and writes its manifests.
# The full recipe (configs/exo_s.yaml) adds: dns_vctk dns_audioset openslr_rirs ears, and DNS read speech through
# Microsoft's own download script (README > Data).
set -euo pipefail
cd "$(dirname "$0")"
python -m exodenoise.data.download data vb_train vb_test demand dns_rir dns_emotional dns_freesound
M="python -m exodenoise.data.manifest"
$M data/manifests/speech_vb.jsonl data/vb_train --min-seconds 1
$M data/manifests/speech_emotional.jsonl data/dns_emotional --min-seconds 1
# DEMAND: four of each recording's sixteen microphones are plenty of variety.
$M data/manifests/noise_demand.jsonl data/demand --exclude 'ch(0[2-46-8]|1[0-24-6])\.wav$'
$M data/manifests/noise_freesound.jsonl data/dns_freesound --min-seconds 1
$M data/manifests/rir_dns.jsonl data/dns_rir
