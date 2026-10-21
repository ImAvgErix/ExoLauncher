using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Settings > Game stores > Notifications and sounds writes Steam's own switches. It fails if a switch Steam reads is
/// left on, a section Steam hasn't written yet is not added, the friends list's JSON is broken or left loud, anything
/// else in the file changes, or a file already quiet is written again.
/// </summary>
public sealed class StoreQuietTests
{
    private const string Shared = """
        "UserRoamingConfigStore"
        {
        	"Software"
        	{
        		"Valve"
        		{
        			"Steam"
        			{
        				"SteamDefaultDialog"		"#app_games"
        				"FriendsUI"
        				{
        					"FriendsUIJSON"		"{\"bNotifications_ShowIngame\":true,\"bSounds_PlayMessage\":true,\"bSounds_PlayOnline\":false,\"nChatFlashMode\":0,\"nChatFontSize\":2,\"strNote\":\"a \\\"b\\\"\"}"
        				}
        			}
        		}
        	}
        	"DisableAllToasts"		"0"
        }

        """;

    private const string Local = """
        "UserLocalConfigStore"
        {
        	"system"
        	{
        		"InGameOverlayShowFPSCorner"		"0"
        	}
        	"friends"
        	{
        		"Notifications_ShowIngame"		"1"
        		"Sounds_PlayMessage"		"1"
        		"ChatFlashMode"		"0"
        	}
        }

        """;

    [Fact]
    public void SharedConfigGoesQuiet()
    {
        var quiet = StoreQuiet.QuietShared(Shared);
        var store = SteamKeyValues.ParseText(quiet).Section("UserRoamingConfigStore");
        Assert.Equal("1", store.Value("DisableAllToasts"));
        Assert.Equal("1", store.Value("DisableToastsInGame"));
        Assert.Equal("0", store.Value("PlaySoundOnToast"));
        Assert.Equal("0", store.Value("AchievementNotificationSound"));
        Assert.Equal("#app_games", store.Section("Software/Valve/Steam").Value("SteamDefaultDialog"));

        var json = System.Text.Json.Nodes.JsonNode.Parse(store.Section("Software/Valve/Steam/FriendsUI").Value("FriendsUIJSON")!)!;
        Assert.False(json["bNotifications_ShowIngame"]!.GetValue<bool>());
        Assert.False(json["bSounds_PlayMessage"]!.GetValue<bool>());
        Assert.Equal(2, json["nChatFlashMode"]!.GetValue<int>());
        Assert.Equal(2, json["nChatFontSize"]!.GetValue<int>());
        Assert.Equal("a \"b\"", json["strNote"]!.GetValue<string>());

        Assert.Equal(quiet, StoreQuiet.QuietShared(quiet));
    }

    [Fact]
    public void LocalConfigGoesQuiet()
    {
        var quiet = StoreQuiet.QuietLocal(Local);
        var store = SteamKeyValues.ParseText(quiet).Section("UserLocalConfigStore");
        Assert.Equal("0", store.Section("friends").Value("Notifications_ShowIngame"));
        Assert.Equal("0", store.Section("friends").Value("Notifications_ShowOnline"));
        Assert.Equal("0", store.Section("friends").Value("Sounds_PlayMessage"));
        Assert.Equal("2", store.Section("friends").Value("ChatFlashMode"));
        Assert.Equal("0", store.Section("system").Value("InGameOverlayScreenshotPlaySound"));
        Assert.Equal("0", store.Section("system").Value("InGameOverlayShowFPSCorner"));
        Assert.Equal("0", store.Section("news").Value("NotifyAvailableGames"));
        Assert.StartsWith("\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"InGameOverlayShowFPSCorner\"\t\t\"0\"\n", quiet.ReplaceLineEndings("\n"));
        Assert.Equal(quiet, StoreQuiet.QuietLocal(quiet));
    }

    [Fact]
    public void FilesAlreadyQuietAreNotEdited()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-quiet-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "userdata", "123", "config");
        var remote = Path.Combine(root, "userdata", "123", "7", "remote");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(remote);
        try
        {
            File.WriteAllText(Path.Combine(config, "localconfig.vdf"), Local);
            File.WriteAllText(Path.Combine(remote, "sharedconfig.vdf"), StoreQuiet.QuietShared(Shared));
            var edits = StoreQuiet.SteamEdits(root);
            Assert.Equal([Path.Combine(config, "localconfig.vdf")], edits.Select(edit => edit.Path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
