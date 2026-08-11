using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class NotificationAreaIconTests
{
    [Fact]
    public void ExplorerRestart_RecreatesOnlyAnIconThatWasShown()
    {
        const uint taskbarCreated = 0xC123;

        Assert.True(NotificationAreaIcon.ShouldRecreateAfterShellRestart(
            taskbarCreated,
            taskbarCreated,
            shown: true));
        Assert.False(NotificationAreaIcon.ShouldRecreateAfterShellRestart(
            taskbarCreated,
            taskbarCreated,
            shown: false));
        Assert.False(NotificationAreaIcon.ShouldRecreateAfterShellRestart(
            taskbarCreated + 1,
            taskbarCreated,
            shown: true));
        Assert.False(NotificationAreaIcon.ShouldRecreateAfterShellRestart(
            taskbarCreated,
            taskbarCreatedMessage: 0,
            shown: true));
    }
}
