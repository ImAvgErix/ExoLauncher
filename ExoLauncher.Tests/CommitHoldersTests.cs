namespace ExoLauncher.Tests;

/// <summary>
/// Ways the line app.log writes when the page runs out of memory (who held Windows' commit) could fail:
/// - a build of many small processes (eight compilers of 2 GB) hides behind one larger program, because each process counts alone;
/// - the same program differs only in case and counts twice;
/// - it names more programs than asked, or the smaller before the larger;
/// - the size reads with a comma on a PC set to another language, so logs don't compare.
/// </summary>
public sealed class CommitHoldersTests
{
    private const long Gb = 1_000_000_000;

    [Fact]
    public void ProcessesOfOneProgramAreSummedAndRankedFirst()
    {
        var rows = Enumerable.Range(0, 8).Select(_ => ("cl", 2 * Gb))
            .Append(("msedgewebview2", 3 * Gb))
            .Append(("CL", 1 * Gb))
            .Append(("node", Gb / 2));
        Assert.Equal("cl x9 17.0 GB, msedgewebview2 3.0 GB", MainWindow.CommitHolders(rows, 2));
    }

    [Fact]
    public void SizesReadTheSameInEveryLanguage()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("node 1.5 GB", MainWindow.CommitHolders([("node", 3 * Gb / 2)], 3));
        }
        finally { Thread.CurrentThread.CurrentCulture = before; }
    }
}
