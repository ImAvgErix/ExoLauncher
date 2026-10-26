using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.mouseCore: Exo Mouse's CPU core panel, relayed as exo.mouseCore by ui/src/lib/mouseRelay.ts, which passes only
/// the action (<see cref="MouseCore"/>). No action reads where Exo's
/// change stands; measure runs the 10-second test (the mouse moving in circles); apply gives the last test's USB
/// controller the CPU it offered (Windows' permission prompt); undo puts it back. Each answer carries the state after
/// it and this session's last test. React never gets a device instance id.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly MouseCore PcMouseCore = new(new InterruptMode.NativeDevices(), new MouseCore.ElevatedWriter(),
        Path.Combine(Helpers.PathHelper.AppDataDir, "mouse-core-backup.json"), InterruptMode.BootTime, () => DateTimeOffset.UtcNow);

    private static int _mouseCoreBusy;

    private static async Task<object> SystemMouseCoreAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        string? message = null;
        if (action is "measure" or "apply" or "undo")
        {
            // One test or change at a time: a second would read the same mouse or race the same key.
            if (Interlocked.Exchange(ref _mouseCoreBusy, 1) == 1) message = "Exo is already on it.";
            else
            {
                try
                {
                    message = await Task.Run(() => action switch
                    {
                        "measure" => MouseCore.Sampler.Run(MouseCore.TestLength, ct) is { } sample
                            ? (PcMouseCore.Measure(sample) is { Verdict: MouseCore.Verdict.Retry } retry ? retry.Detail : null)
                            : "Exo couldn't read your mouse.",
                        "apply" => PcMouseCore.Apply(),
                        _ => PcMouseCore.Undo(),
                    }, ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _mouseCoreBusy, 0);
                }
            }
        }
        var state = await Task.Run(PcMouseCore.Read, ct).ConfigureAwait(false);
        var last = PcMouseCore.Last;
        return new
        {
            ok = message is null,
            message,
            stage = state.Stage.ToString().ToLowerInvariant(),
            detail = state.Detail,
            cpu = state.Cpu,
            better = state.Better,
            canUndo = state.CanUndo,
            before = Dto(state.Before),
            after = Dto(state.After),
            test = last is null ? null : new
            {
                verdict = last.Verdict.ToString().ToLowerInvariant(),
                detail = last.Detail,
                offer = last.Offer,
                result = Dto(last.Test),
            },
        };
    }

    private static object? Dto(MouseCore.Test? t) => t is null ? null : new
    {
        rate = t.Rate,
        @checked = t.Checked,
        missed = t.Missed,
        longest = t.Longest,
        p999 = t.P999,
        refresh = t.Refresh,
        cpu = t.Cpu,
    };
}
