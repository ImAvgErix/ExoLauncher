# Backend recovery and performance validation

The recovery harness (`tools/Test-BackendRecovery.ps1` and its test assembly) has been
removed, and the repo has no test project. This page records the method behind the
retained 2026-09-07 results; it cannot be rerun from this tree.

The recovery fixtures use temporary application data, fake stores and HTTP responses,
and inert child processes. They cover failed atomic writes, interrupted downloads,
account changes, stale publication, rate limits, cancellation and retry limits.
They do not install games, redeem keys, write to hardware, or use saved store sessions.

The opt-in profile uses a fixed synthetic library of 10,000 titles and 20,000 source
rows. It records source grouping, cold application cache parsing, warm preference
projection and repeated library searches. The long-session scenario measures 1,000
warm library interactions in ten consecutive batches. Cold means a fresh JSON
deserialization, not a flushed operating-system disk cache. No network, artwork
downloads, store process or UI-rendering time is included.

`backend-profile.json` records raw samples, p50/p95/p99 duration, allocated bytes,
managed heap and private-memory change, per-batch memory samples, runtime, operating
system, architecture, processor description, configuration and fixture sizes. Compare
matching scenarios, configuration and machine characteristics. Post-GC memory is
reported separately from process private bytes; neither is a claim of a leak by itself.

This establishes a reproducible baseline without inventing timing thresholds. Retain
the first result and compare future runs on the same runner class before selecting a
release threshold. Functional assertions remain mandatory regardless of measured time.
