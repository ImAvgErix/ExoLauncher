# Exo Capture

Instant replay, recording, snips and Go Live for Exo Launcher. C++20, GPL-3.0-or-later. Exo starts it and talks to it
over standard input and output only: one JSON object per line. Requests are `{"id", "method", "params"}`, replies
`{"id", "result"}` or `{"id", "error"}`, events `{"event", "data"}` (Exo shows them as `capture.<event>`, so the
`saved` event is `capture.saved`).

Build: `pwsh -File capture\build.ps1` (Visual Studio Build Tools, CMake, Ninja) writes `capture\out\ExoCapture.exe`.
`pwsh -File capture\build.ps1 -Test` also builds and runs the Shrink self-test (below); options after `-Test` go to it.

## Shrink

`shrink` re-encodes a finished recording to a smaller file that looks the same, and proves it before saying so. The
live recorder holds a fixed bit rate on a GPU encoder with no time to think; shrink has time: the GPU's encoder at its
most careful setting, constant quality (or, where the encoder has none, a variable rate near 60% of the recording's),
B-frames where the encoder offers them, a key frame every 3 s. Measured 55 to 70% smaller (ratio 0.31 to 0.45) on this PC. Audio is not re-encoded: every track's AAC packets are
copied as they are. The original is never touched, moved or deleted: that is the caller's decision.

### Methods

`shrink` `{ input, output?, codec?, targetQuality? }`

| Param | |
| --- | --- |
| `input` | A recording in the captures folder. |
| `output` | Where to write. Default `<name>.shrink.mp4` beside the input. Must be in the captures folder and must not exist (the default name is replaced). |
| `codec` | `"auto"` (default; AV1, else HEVC, else H.264: the best this GPU encodes **and** Windows can play back), `"av1"`, `"hevc"` or `"h264"` as a ceiling. A codec the GPU lacks steps down; one worse than the recording's own is never used. |
| `targetQuality` | 0 to 100, default 70. Higher stays closer to the original and saves less. |

The reply comes when it is over (minutes for a long recording; it runs at below-normal priority, so keep waiting):

```json
{ "ok": true, "input": "...mp4", "output": "...shrink.mp4", "inputBytes": 0, "outputBytes": 0, "ratio": 0.0,
  "codec": "HEVC", "seconds": 0.0, "ssim": { "mean": 0.0, "min": 0.0 },
  "encoder": "NVIDIA HEVC Encoder MFT", "mode": "quality", "frames": 0 }
```

`ratio` is `outputBytes / inputBytes`. `mode` is how the encoder was driven: `quality` (constant quality), `vbr` or `cbr`.
`ssim` is the luma SSIM of 24 evenly spaced frames, old against new (`min` is the worst frame).

When the original should stay as it is, `ok` is false, no output file exists, and `skipped` says why (`detail` has words):

| `skipped` | Meaning | Try again? |
| --- | --- | --- |
| `not-smaller` | The new file saved under 15%. | No. |
| `quality-loss` | SSIM under 0.985 mean or 0.95 for the worst frame. | Maybe, with a higher `targetQuality`. |
| `hdr-unsupported` | HDR or 10-bit source this GPU's encoder or the MP4 writer cannot carry whole (never turned into SDR). | No. |
| `audio-passthrough`, `unsupported-tracks` | A track cannot be copied as it is, or the file has a track kind other than one video and sound. | No. |
| `audio-changed`, `audio-tracks`, `duration`, `frame-count`, `frame-time`, `decode-error`, `color-mismatch`, `picture-size`, `output-unreadable` | A check of the new file failed (an encoder fault). | Maybe, with another `codec`. |
| `no-encoder`, `no-better-encoder` | No GPU encoder here, or none better than the recording's own. | No. |
| `source-unreadable`, `not-mp4`, `unsupported-video`, `not-found` | The recording cannot be read or is not H.264, HEVC or AV1 in an MP4. | No. |
| `recording-in-progress` | `<name>.mp4.exo-rec` exists: the recording is still open or waits to be recovered. | Later. |
| `already-shrunk`, `output-exists`, `bad-output` | The names clash. | No. |
| `busy` | Another shrink is running; one at a time. | Later. |
| `cancelled` | `shrink.cancel` (or shutdown). | If wanted. |

A failure of Windows itself comes back as an ordinary `{"id", "error"}`; the partial file is deleted then too.

`shrink.cancel` `{ input? }` stops the running shrink (the one for `input`, if given). Replies `true` if one was running.
The shrink then replies `skipped: "cancelled"`, within a second, with the partial file deleted.

### Events

`shrink` `{ input, percent }`: at most twice a second while a shrink runs. 0 to 90 is encoding, 90 to 99 checking the
new file. The reply is the end; there is no 100.

### What is checked before `ok`

All of: at least 15% smaller; the same tracks (one video, the same number of sound tracks, the same order); duration
within 0.25 s; every audio packet of every track byte-identical and within 1 ms of its old time; for HDR or 10-bit the
same bit depth, color primaries, transfer function, matrix, range and mastering/light-level boxes; same picture size;
the whole new video decodes without error; frame count within 1%; and SSIM of 24 sampled frames, mean at least 0.985 and
worst at least 0.95. The new file is written as `<output>.part` and renamed only after all of that.

### How a host should use it

Call it for recordings that are finished and idle (no `.exo-rec` beside them), one at a time, when the PC is not in a
game. On `ok`, check that `output` exists, then replace the original by renaming (keep the original until the new file
is in the library). On any `skipped`, keep the original and do not call again for the same file unless the table says
maybe; remember the answer so the same file is not retried every day.

### Self-test

`ExoCaptureShrinkTest.exe [--dir <folder>] [--keep] [--real <recording.mp4>] [--no-hdr] [--quick]` (also `--dump <file>`
and `--codecapi`, to look at a file's packets or what the GPU's encoders accept; built by
`build.ps1 -Test`, kept out of `ExoCapture.exe`). It makes a 1080p60 recording with the recorder's own encoder and
muxer (two AAC tracks), shrinks it, measures the result with code that does not share Shrink's, proves the checks fail
on over-compressed, truncated, track-dropped and altered files, tests cancel and progress, and prints a table.
`--real` shrinks a copy of a recording made by Exo Capture. Exit code 0 means every check passed.
