// Attack tests mutate process-wide state (EXO_LAUNCHER_DATA_DIR) and the shared
// cover/upscaler caches. Run them serially so one test's environment never
// bleeds into another.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
