// The harness ends only the throwaway ping.exe copies it started (one is named vgc.exe on purpose).
#pragma warning disable RS0030
using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 3 — can a decoy process (fake Discord.exe/Spotify.exe/vgc.exe in
/// temp, or a reused PID) make Exo trim/freeze/kill the wrong or a protected
/// process? Exercises the REAL ownership predicates in
/// Helpers/ManagedProcessResources.cs (HostedGroup, AllAlive, StartedAt via
/// WorkingSetBytes) with real temp processes.
/// </summary>
internal static class Attack3_ProcessOwnership
{
    internal static void Run(string argvEchoExe)
    {
        Check.Section("Attack 3 — process ownership / kill / freeze identity");

        // A standalone native exe so the decoy runs after being copied/renamed
        // anywhere (the E2E harness copies ping.exe for the same reason).
        var nativeStub = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        string[] StubArgs = ["-n", "600", "127.0.0.1"]; // ~10 min; killed in finally

        // Stage a decoy "Discord.exe" OUTSIDE any real Discord install dir.
        var decoyDirA = Path.Combine(Path.GetTempPath(), "exo-attack3-A-" + Guid.NewGuid().ToString("N"));
        var decoyDirB = Path.Combine(Path.GetTempPath(), "exo-attack3-B-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(decoyDirA);
        Directory.CreateDirectory(decoyDirB);
        var fakeDiscord = Path.Combine(decoyDirA, "Discord.exe");
        var fakeVgc = Path.Combine(decoyDirA, "vgc.exe");
        File.Copy(nativeStub, fakeDiscord, true);
        File.Copy(nativeStub, fakeVgc, true);

        Process? decoy = null, vgc = null;
        try
        {
            ProcessStartInfo Psi(string exe) => new()
            {
                FileName = exe, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { StubArgs[0], StubArgs[1], StubArgs[2] },
            };
            decoy = Process.Start(Psi(fakeDiscord));
            vgc = Process.Start(Psi(fakeVgc));
            Thread.Sleep(400);
            var decoyId = ManagedProcessResources.Identify(decoy!.Id)!.Value;

            // (1) A process whose exe is NOT under the client's install dir is
            // excluded from the group => never trimmed/throttled/killed.
            var groupWrongDir = ManagedProcessResources.HostedGroup("discord", decoyId, decoyDirB,
                auxiliaryNames: new[] { "Discord" });
            Check.That("Decoy outside install dir is NOT grouped",
                groupWrongDir.Processes.Count == 0,
                $"HostedGroup(dir={Path.GetFileName(decoyDirB)}) returned {groupWrongDir.Processes.Count} members for a Discord.exe living in dir A");

            // Sanity: path-gating includes it only when the dir actually matches.
            var groupRightDir = ManagedProcessResources.HostedGroup("discord", decoyId, decoyDirA,
                auxiliaryNames: new[] { "Discord" });
            Check.That("Grouping is by install-dir prefix, not by name",
                groupRightDir.Processes.Any(p => p.Pid == decoy.Id),
                $"same Discord.exe IS grouped when directory == its own folder ({groupRightDir.Processes.Count} members)");

            // (2) Anti-cheat (vgc.exe) is never a descendant of a hosted client
            // nor under its dir, so it is never in a kill group.
            var discordGroupForKill = ManagedProcessResources.HostedGroup("discord", decoyId, decoyDirB,
                auxiliaryNames: new[] { "Discord", "vgc", "vgk", "Vanguard" });
            Check.That("Anti-cheat (vgc) never joins a hosted-client group",
                discordGroupForKill.Processes.All(p => p.Pid != vgc!.Id),
                "even listed as an auxiliary name, vgc.exe outside the install dir is excluded by the path prefix check");

            // (3) PID reuse / TOCTOU: identity carries the process creation time.
            // A fabricated identity with the right PID but a wrong start time is
            // rejected by StartedAt (so a reused PID is never acted on). This is
            // the same guard WaitThenKill re-checks before Process.Kill().
            var spoofed = new ManagedProcessIdentity(decoy.Id, decoyId.StartedUtcTicks + 1);
            Check.That("Wrong start-time identity is rejected (PID-reuse guard)",
                ManagedProcessResources.WorkingSetBytes(spoofed) is null,
                "WorkingSetBytes(pid, wrongTicks) == null although the PID is alive");
            var realGroup = new ManagedProcessGroup("discord", new[] { decoyId });
            Check.That("AllAlive true for the genuine identity", ManagedProcessResources.AllAlive(realGroup),
                "genuine (pid, startTime) passes AllAlive");
            var spoofGroup = new ManagedProcessGroup("discord", new[] { spoofed });
            Check.That("AllAlive false for spoofed start-time", !ManagedProcessResources.AllAlive(spoofGroup),
                "(pid, wrongTicks) fails AllAlive even though the PID is running");

            // (4) After the real process exits, its identity goes dead: Exo
            // rebuilds the group rather than acting on whatever took the PID.
            // AllAlive is the liveness guard the trim/throttle paths use.
            decoy.Kill();
            decoy.WaitForExit(5000);
            Check.That("Dead identity reports not-alive",
                !ManagedProcessResources.AllAlive(realGroup),
                "once the identified process exits, AllAlive(group)=false (signaled-handle check)");
        }
        finally
        {
            foreach (var p in new[] { decoy, vgc })
                try { if (p is { HasExited: false }) p.Kill(); } catch { } finally { p?.Dispose(); }
            try { Directory.Delete(decoyDirA, true); } catch { }
            try { Directory.Delete(decoyDirB, true); } catch { }
        }
    }
}
