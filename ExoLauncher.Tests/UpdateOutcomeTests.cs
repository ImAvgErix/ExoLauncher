using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a failed update could stay silent or say too much:
/// - a rolled-back or half-finished update says nothing, and the same version is offered again with no reason;
/// - a successful one is reported as a problem;
/// - the installer's own words (they name paths and folders) reach the page;
/// - an unreadable note throws instead of saying nothing.
/// </summary>
public sealed class UpdateOutcomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-outcome-" + Guid.NewGuid().ToString("N"));

    public UpdateOutcomeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData("""{"schema":1,"version":"3.16.0","status":"rolled-back","message":"C:/Users/x/app is busy"}""", "The last update (v3.16.0) didn't start, so Exo went back to the version before.")]
    [InlineData("""{"schema":1,"version":"3.16.0","status":"failed-preserved"}""", "The last update (v3.16.0) didn't finish; the version before is kept.")]
    public void AnUpdateThatDidNotTakeIsSaid(string json, string said) => Assert.Equal(said, UpdateOutcome.Say(json, null));

    [Fact]
    public void ASuccessIsNotAProblem() =>
        Assert.Null(UpdateOutcome.Say("""{"schema":1,"version":"3.16.0","status":"succeeded"}""", null));

    [Fact]
    public void ASilentInstallerFailureIsSaidFromItsLogAlone() =>
        Assert.Equal("The last update didn't install.", UpdateOutcome.Say(null, "C:/Users/x/ExoLauncher: access denied"));

    [Fact]
    public void NothingLeftBehindSaysNothing()
    {
        Assert.Null(UpdateOutcome.Say(null, null));
        Assert.Null(UpdateOutcome.Say("not json", "  "));
    }

    [Fact]
    public void TheWordsToThePageNeverCarryThePathsOfTheInstallersNote()
    {
        var said = UpdateOutcome.Say("""{"status":"rolled-back","version":"3.16.0","message":"C:/Users/x/secret"}""", "C:/Users/x/secret");
        Assert.DoesNotContain("secret", said);
        Assert.DoesNotContain("C:/", said);
    }

    [Fact]
    public void TakeReadsAndRemovesTheNotes()
    {
        File.WriteAllText(Path.Combine(_root, UpdateOutcome.ResultFile), """{"status":"failed-preserved","version":"3.16.0"}""");
        File.WriteAllText(Path.Combine(_root, UpdateOutcome.ErrorFile), "the replacement did not start");
        Assert.Equal("The last update (v3.16.0) didn't finish; the version before is kept.", UpdateOutcome.Take(_root));
        Assert.False(File.Exists(Path.Combine(_root, UpdateOutcome.ResultFile)));
        Assert.False(File.Exists(Path.Combine(_root, UpdateOutcome.ErrorFile)));
        Assert.Null(UpdateOutcome.Take(_root));
    }
}
