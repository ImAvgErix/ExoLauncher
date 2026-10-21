using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways hosted-process ownership can fail:
/// - a child that started before its supposed parent (the parent's pid was reused) is taken as a child;
/// - an unreadable start time (0) on either side is treated as "older" and drops a real child;
/// - equal start times are treated as "older";
/// - Exo itself is counted in another process's tree;
/// - a descendant reached twice (a cycle from pid reuse) loops or is listed twice;
/// - pid 0 (the idle process) is listed;
/// - a rejected child's own children are still listed.
/// Start times come from a pre-filled cache, so no process is opened.
/// </summary>
public sealed class BackgroundTrimTests
{
    [Theory]
    [InlineData(100L, 200L, true)]   // child older than parent: pid reuse
    [InlineData(200L, 100L, false)]  // normal order
    [InlineData(150L, 150L, false)]  // same instant is not "before"
    [InlineData(0L, 100L, false)]    // child unreadable: keep the table's word
    [InlineData(100L, 0L, false)]    // parent unreadable: keep the table's word
    public void StartedBeforeNeedsBothTimes(long childStart, long parentStart, bool expected)
    {
        var cache = new Dictionary<int, long> { [10] = childStart, [20] = parentStart };
        Assert.Equal(expected, BackgroundTrim.StartedBefore(10, 20, cache));
    }

    [Fact]
    public void TreeSkipsReusedPidStrangersAndTheirChildren()
    {
        const int self = 999;
        var cache = new Dictionary<int, long>
        {
            [1] = 1_000, [2] = 1_100, [3] = 1_200,
            [4] = 500,   // older than root 1: its real parent exited and root reused the pid
            [5] = 600,   // child of the stranger
            [self] = 1_300,
        };
        (int pid, int parent)[] table = [(1, 0), (2, 1), (3, 2), (4, 1), (5, 4), (self, 1), (0, 1)];
        var found = BackgroundTrim.Descendants(1, table, (child, parent) => BackgroundTrim.IsTreeChild(child, parent, self, cache));
        Assert.Equal([2, 3], found);
    }

    [Fact]
    public void CycleFromPidReuseTerminates()
    {
        var cache = new Dictionary<int, long> { [1] = 10, [2] = 20 };
        (int pid, int parent)[] table = [(2, 1), (1, 2)];
        var found = BackgroundTrim.Descendants(1, table, (child, parent) => BackgroundTrim.IsTreeChild(child, parent, -1, cache));
        Assert.Equal([2], found);
    }

    // A call (or other live sound) running in Exo while a game is in front: the host must not drop below normal
    // (its audio threads would rank under every thread of the game) nor have its working set emptied (the noise
    // model would page out mid-call). Without one, both still yield to the game as before.
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void TheHostYieldsToAGameExceptDuringACall(bool gameRunning, bool windowActive, bool liveAudio, bool yields) =>
        Assert.Equal(yields, HostPriorityPolicy.Wanted(gameRunning, windowActive, liveAudio));

    [Fact]
    public void AHiddenHostKeepsItsWorkingSetDuringACall()
    {
        var host = new ManagedProcessIdentity(4242, 1);
        ManagedProcessGroup[] groups = [new("exo", [host])];
        var hidden = new HostedTrimPolicy(MusicRoomOpen: false, MusicPlaying: false, ShellHidden: true);
        Assert.Contains(host, ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), hidden));
        Assert.DoesNotContain(host, ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), hidden with { LiveAudio = true }));
    }
}
