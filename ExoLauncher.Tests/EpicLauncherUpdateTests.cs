using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>Updating a game Epic's new launcher installed: what its install log says, and the link that asks it to update.</summary>
public sealed class EpicLauncherUpdateTests
{
    // Rocket League as the launcher left it: Exo's Legendary had patched the files, but the launcher's newest line
    // still carried the update it started (pendingVersion), so Exo showed Update forever.
    [Fact]
    public void AnUpdateTheLauncherStartedIsPendingUntilItsLineSaysItLanded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-eglv2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "installations.jsonl"), [
                """{"version":1}""",
                """{"installationId":"5E5A","complete":true,"dir":"C:\\Games\\rocketleague","namespace":"9773","itemId":"530d","artifactId":"Sugar","version":"++Prime+Update60-CL-529371"}""",
                """{"installationId":"5E5A","complete":false,"dir":"C:\\Games\\rocketleague","namespace":"9773","itemId":"530d","artifactId":"Sugar","version":"++Prime+Update60-CL-529371","pendingVersion":"++Prime+Update60.1-CL-530866"}""",
            ]);
            var pending = Assert.Single(EpicLauncherV2.ReadInstallations(dir));
            Assert.Equal(("5E5A", "++Prime+Update60.1-CL-530866", false), (pending.Id, pending.PendingVersion, pending.UpToDate));

            File.AppendAllLines(Path.Combine(dir, "installations.jsonl"), [
                """{"installationId":"5E5A","complete":true,"dir":"C:\\Games\\rocketleague","namespace":"9773","itemId":"530d","artifactId":"Sugar","version":"++Prime+Update60.1-CL-530866"}""",
            ]);
            var landed = Assert.Single(EpicLauncherV2.ReadInstallations(dir));
            Assert.True(landed.UpToDate);
            Assert.Equal("++Prime+Update60.1-CL-530866", landed.Version);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheLauncherIsAskedToUpdateTheGameByItsFullId()
    {
        var link = EpicAdapter.BuildEpicActionUris("Sugar", "9773", "530d", "update").First();
        Assert.Equal("com.epicgames.launcher://apps/9773%3A530d%3ASugar?action=update", link);
    }
}
