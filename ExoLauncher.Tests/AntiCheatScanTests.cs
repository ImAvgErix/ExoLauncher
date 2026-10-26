using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the anti-cheat scan could clear a game that has one (the swap then edits files an anti-cheat watches):
/// - the anti-cheat folder is a junction or link, which the walk skipped before reading its name;
/// - a marker file or folder is found only at the top, not in the game's subfolders;
/// - a plain folder with an unrelated name is taken for anti-cheat.
/// </summary>
public sealed class AntiCheatScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-ac-" + Guid.NewGuid().ToString("N"));

    public AntiCheatScanTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string? Scan()
    {
        string? antiCheat = null;
        DlssSwapService.Walk(_root, [], ref antiCheat, 0, CancellationToken.None);
        return antiCheat;
    }

    [Fact]
    public void ARealAntiCheatFolderIsFoundDeepInTheInstall()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Game", "Binaries", "EasyAntiCheat"));
        Assert.Equal("Easy Anti-Cheat", Scan());
    }

    [Fact]
    public void AFolderWithAnUnrelatedNameIsNotAntiCheat()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Game", "Engine", "Plugins"));
        Assert.Null(Scan());
    }

    [Fact]
    public void AJunctionNamedLikeAnAntiCheatStillCounts()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        var game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(game);
        Assert.SkipUnless(TryCreateJunction(Path.Combine(game, "BattlEye"), outside), "This volume takes no junctions.");
        Assert.Equal("BattlEye", Scan());
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
