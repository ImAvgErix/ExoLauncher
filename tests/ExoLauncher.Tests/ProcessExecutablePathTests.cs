using System.Diagnostics;
using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ProcessExecutablePathTests
{
    [Fact]
    public void TryGetExecutablePath_ResolvesCurrentProcess()
    {
        using var self = Process.GetCurrentProcess();
        var path = ProcessHelper.TryGetExecutablePath(self);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
        Assert.Contains("ExoLauncher.Tests", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetExecutablePath_ResolvesForeignProcessByPid()
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 8 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(proc);
        try
        {
            var path = ProcessHelper.TryGetExecutablePath(proc.Id);
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.True(File.Exists(path));
            Assert.EndsWith("ping.exe", path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { /* cleanup */ }
        }
    }
}

