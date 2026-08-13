using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Live process stop coverage — the unit eligibility tests never proved that
/// QueryFullProcessImageName + tree kill actually closes a running title.
/// </summary>
public sealed class GameProcessRegistryStopLiveTests
{
    [Fact]
    public async Task StopAsync_ClosesObservedProcessUnderInstallRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var gameExe = Path.Combine(root, "Game-Win64-Shipping.exe");
        File.Copy(source, gameExe, overwrite: true);

        var game = new GameEntry
        {
            Id = "steam:stop-live-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Stop Live Fixture",
            Store = StoreKind.Steam,
            Installed = true,
            Path = root,
            LaunchTarget = "999001",
        };

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = gameExe,
            Arguments = "-n 90 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(process);

        try
        {
            // Path resolution must work without MainModule for Stop eligibility.
            var image = ProcessHelper.TryGetExecutablePath(process);
            Assert.False(string.IsNullOrWhiteSpace(image));
            Assert.True(ProcessHelper.IsPathUnderRoot(image!, root));

            var registry = new GameProcessRegistry();
            registry.ObserveLaunch(game, process.Id);

            var observed = registry.GetState(game, discoverExternal: false);
            Assert.True(observed.IsRunning);
            Assert.True(observed.CanStop);

            var result = await registry.StopAsync(game);
            Assert.True(result.Ok, result.Message);

            var exited = process.WaitForExit(10_000);
            Assert.True(exited || process.HasExited);

            var after = registry.GetState(game, discoverExternal: true);
            Assert.False(after.CanStop);
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* cleanup */ }
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task StopAsync_DiscoversExternalProcessWithoutPriorObserve()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var gameExe = Path.Combine(root, "Title-Win64-Shipping.exe");
        File.Copy(source, gameExe, overwrite: true);

        var game = new GameEntry
        {
            Id = "steam:stop-ext-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Stop External Fixture",
            Store = StoreKind.Steam,
            Installed = true,
            Path = root,
            LaunchTarget = "999002",
        };

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = gameExe,
            Arguments = "-n 90 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(process);

        try
        {
            // Simulate Steam-launched title Exo never observed at handoff.
            var registry = new GameProcessRegistry();
            var external = registry.GetState(game, discoverExternal: true);
            Assert.True(external.CanStop);

            var result = await registry.StopAsync(game);
            Assert.True(result.Ok, result.Message);
            Assert.True(process.WaitForExit(10_000) || process.HasExited);
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* cleanup */ }
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }
}
