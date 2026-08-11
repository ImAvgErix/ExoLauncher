using Xunit;

namespace ExoLauncher.Tests;

public sealed class InstallerContractTests
{
    [Fact]
    public void Setup_DefaultNameCannotCollideWithInstalledProcess()
    {
        var script = ReadInstaller();

        Assert.Contains("!define OUTFILE \"ExoLauncher-Setup.exe\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill /F /IM ExoLauncher.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExecutablePath", script, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::OrdinalIgnoreCase.Equals", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $$process.ProcessId -Force", script, StringComparison.Ordinal);
        Assert.Contains("-File \"$R2\"", script, StringComparison.Ordinal);
        Assert.Contains("!searchparse /file \"..\\VERSION\"", script, StringComparison.Ordinal);
        Assert.Contains("$$env:EXO_SILENT_INSTALL -eq '1'", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("$$graceDeadline", StringComparison.Ordinal) <
            script.IndexOf("Stop-Process -Id $$process.ProcessId -Force", StringComparison.Ordinal),
            "In-app setup must wait for Exo's normal shutdown before its exact-path force-stop fallback.");
    }

    [Fact]
    public void Setup_IsPinnedToManagedAppDirectory_AndRefusesUnmanagedContents()
    {
        var script = ReadInstaller();

        Assert.DoesNotContain("MUI_PAGE_DIRECTORY", script, StringComparison.Ordinal);
        Assert.Contains("StrCpy $INSTDIR \"$LOCALAPPDATA\\ExoLauncher\\app\"", script, StringComparison.Ordinal);
        Assert.Contains("IfFileExists \"$INSTDIR\\*.*\" unmanaged_target target_empty", script, StringComparison.Ordinal);
        Assert.Contains("IfFileExists \"$INSTDIR\\ExoLauncher.exe\" 0 uninstall_registry", script, StringComparison.Ordinal);
        Assert.Contains("app.incoming-$0", script, StringComparison.Ordinal);
        Assert.Contains("app.previous-$0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RMDir /r \"$INSTDIR.old\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_UsesRollbackSwapAndRelaunchesSilentUpdates()
    {
        var script = ReadInstaller();

        Assert.Contains("Rename \"$INSTDIR\" \"$R5\"", script, StringComparison.Ordinal);
        Assert.Contains("Rename \"$R9\" \"$INSTDIR\"", script, StringComparison.Ordinal);
        Assert.Contains("Rename \"$R5\" \"$INSTDIR\"", script, StringComparison.Ordinal);
        Assert.Contains("SetOutPath \"$TEMP\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback: copy over in place", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IfSilent silent_launch install_done", script, StringComparison.Ordinal);
        Assert.Contains("Exec '\"$INSTDIR\\ExoLauncher.exe\"'", script, StringComparison.Ordinal);
        Assert.Contains("silent_install_fail:", script, StringComparison.Ordinal);
        Assert.Contains("update-error.log", script, StringComparison.Ordinal);
        Assert.Contains("Delete \"$R8\\update-error.log\"", script, StringComparison.Ordinal);
        Assert.Contains("SetErrorLevel 1", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", script, StringComparison.Ordinal);
    }

    private static string ReadInstaller()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "ExoLauncher.nsi"));
    }
}
