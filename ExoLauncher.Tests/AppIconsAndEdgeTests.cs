using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// System › Apps' icon finder and Edge's removal. Ways they could fail: a program's name doesn't match its own shortcut or
/// exe once versions and bitness are in it ("Python 3.13 (64-bit)"), so it shows a letter; Edge's elevated steps leave
/// the stub Exo made in SystemApps, take away one that was there, name WebView2's app id in the policy, or close Exo's
/// own WebView2; the WebView2 runtime reads as Edge and goes.
/// </summary>
public sealed class AppIconsAndEdgeTests
{
    [Theory]
    [InlineData("Python 3.13.9 (64-bit)", "python")]
    [InlineData("Equalizer APO 1.4.2", "equalizer apo")]
    [InlineData("Node.js", "node.js")]
    [InlineData("GitHub CLI", "github cli")]
    [InlineData("7-Zip 26.00 (x64)", "7 zip")]
    public void PlainNamesCompareWithoutVersionsOrBitness(string name, string plain) => Assert.Equal(plain, ProgramIcons.Plain(name));

    [Fact]
    public void EdgeIsTheBrowserNotWebViewOrItsUpdater()
    {
        Assert.True(EdgeRemoval.IsEdge("Microsoft Edge", @"""C:\Program Files (x86)\Microsoft\Edge\Application\156.0.4314.42\Installer\setup.exe"" --uninstall --msedge --channel=stable --system-level --verbose-logging"));
        Assert.False(EdgeRemoval.IsEdge("Microsoft Edge WebView2 Runtime", @"""C:\Program Files (x86)\Microsoft\EdgeWebView\Application\156.0\Installer\setup.exe"" --uninstall --msedgewebview --system-level"));
        Assert.False(EdgeRemoval.IsEdge("Microsoft Edge Update", "MicrosoftEdgeUpdate.exe /uninstall"));
        Assert.True(EdgeRemoval.IsWebView("Microsoft Edge WebView2 Runtime"));
        Assert.False(EdgeRemoval.IsWebView("Microsoft Edge"));
    }

    [Fact]
    public void EdgeStepsMakeAndTakeBackOnlyTheirOwnStub()
    {
        const string setup = @"C:\Program Files (x86)\Microsoft\Edge\Application\156.0.4314.42\Installer\setup.exe";
        var made = EdgeRemoval.Commands(setup, stubThere: false, folderThere: false);
        Assert.StartsWith("reg add", made[0], StringComparison.Ordinal);   // cmd /c keeps the quotes of a line that doesn't open with one
        Assert.Contains(made, step => step.StartsWith("mkdir ", StringComparison.Ordinal));
        Assert.Contains(made, step => step.StartsWith("type nul > ", StringComparison.Ordinal));
        Assert.Contains(made, step => step.StartsWith("del /f /q ", StringComparison.Ordinal));
        Assert.Contains(made, step => step.StartsWith("rd ", StringComparison.Ordinal));
        var uninstall = made.Single(step => step.Contains("setup.exe", StringComparison.Ordinal));
        Assert.Equal($@"""{setup}"" --uninstall --msedge --channel=stable --system-level --verbose-logging --force-uninstall", uninstall);
        Assert.True(made.ToList().IndexOf(uninstall) > made.ToList().FindIndex(step => step.StartsWith("type nul", StringComparison.Ordinal)));

        var there = EdgeRemoval.Commands(setup, stubThere: true, folderThere: true);
        Assert.DoesNotContain(there, step => step.StartsWith("mkdir ", StringComparison.Ordinal) || step.StartsWith("type nul", StringComparison.Ordinal)
            || step.StartsWith("del ", StringComparison.Ordinal) || step.StartsWith("rd ", StringComparison.Ordinal));

        // Edge's own windows close; Exo's WebView2 (msedgewebview2.exe) is another image and stays.
        Assert.Contains("taskkill /im msedge.exe /f", there);
        var policy = there.Single(step => step.Contains(@"Policies\Microsoft\EdgeUpdate", StringComparison.Ordinal));
        Assert.Contains("Install{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("F3017226", policy, StringComparison.Ordinal);
        // The bar on reinstalling goes on only once Edge's browser is gone, never over a setup that refused.
        Assert.StartsWith($@"if not exist ""{EdgeRemoval.Browser(setup)}"" reg add", policy, StringComparison.Ordinal);
        Assert.EndsWith(@"\Edge\Application\msedge.exe", EdgeRemoval.Browser(setup), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupMustBeEdgesOwn()
    {
        Assert.Null(EdgeRemoval.Setup(@"""C:\Users\Public\setup.exe"" --uninstall --msedge"));
        Assert.Null(EdgeRemoval.Setup(@"""C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1\Installer\setup.exe"" --uninstall --msedge"));
    }
}
