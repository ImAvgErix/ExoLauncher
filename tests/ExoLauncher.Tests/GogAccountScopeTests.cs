using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GogAccountScopeTests
{
    [Fact]
    public void AccountScopeFor_UsesAnOpaqueStablePerUserKey()
    {
        var first = GogAdapter.AccountScopeFor(new GogdlCli.AuthCredentials(
            "access-token", "gog-user-a", "refresh-token", null));
        var same = GogAdapter.AccountScopeFor(new GogdlCli.AuthCredentials(
            "new-access-token", "gog-user-a", "new-refresh-token", null));
        var other = GogAdapter.AccountScopeFor(new GogdlCli.AuthCredentials(
            "access-token", "gog-user-b", "refresh-token", null));

        Assert.NotNull(first);
        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("gog-user-a", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsafe\nuser")]
    public void AccountScopeFor_FailsClosedForUnsafeOrMissingUsers(string? userId)
    {
        var credentials = userId is null
            ? null
            : new GogdlCli.AuthCredentials("access-token", userId, "refresh-token", null);

        Assert.Null(GogAdapter.AccountScopeFor(credentials));
    }
}
