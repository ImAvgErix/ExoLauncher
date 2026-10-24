using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// A role's Linked Roles requirements as Exo passes them to Discord, which fails silently: more than Discord's 10
/// requirements sent (Discord refuses the whole save); a check missing how it compares or with what reaching Discord as
/// half a requirement, or an app requirement without its app; an app's own key (digits allowed) or a negative count
/// dropped; text that isn't a connection type, key, id or number passed through; an empty group sent as a requirement of
/// nothing.
/// </summary>
public sealed class DiscordRoleLinksTests
{
    private static JsonArray Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WebHostBridge.RoleLinks(doc.RootElement.Clone());
    }

    [Fact]
    public void AnAccountAndItsChecksPassAsDiscordSavesThem()
    {
        var groups = Read("""[[{"connection_type":"steam"},{"connection_type":"steam","connection_metadata_field":"game_count","operator":4,"value":"9"}]]""");
        var items = Assert.Single(groups)!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Null(items[0]!["connection_metadata_field"]);
        Assert.Null(items[0]!["operator"]);
        Assert.Equal("game_count", (string)items[1]!["connection_metadata_field"]!);
        Assert.Equal(4, (int)items[1]!["operator"]!);
        Assert.Equal("9", (string)items[1]!["value"]!);
    }

    [Fact]
    public void AnAppsOwnKeysAndNegativeCountsPass()
    {
        var items = Read("""[[{"connection_type":"application","application_id":"1234567890","connection_metadata_field":"level_2","operator":1,"value":"-3"}]]""")[0]!.AsArray();
        Assert.Equal("1234567890", (string)items[0]!["application_id"]!);
        Assert.Equal("level_2", (string)items[0]!["connection_metadata_field"]!);
        Assert.Equal("-3", (string)items[0]!["value"]!);
    }

    [Fact]
    public void HalfAChecksAndAnythingElseIsLeftOut()
    {
        var groups = Read("""
            [[{"connection_type":"steam","connection_metadata_field":"game_count","operator":4},
              {"connection_type":"steam","connection_metadata_field":"game_count","value":"3"},
              {"connection_type":"<b>"},
              {"connection_type":"x"},
              {"connection_type":"reddit","connection_metadata_field":"karma\"","operator":4,"value":"1"},
              {"connection_type":"reddit","connection_metadata_field":"total_karma","operator":7,"value":"1"},
              {"connection_type":"reddit","connection_metadata_field":"total_karma","operator":4,"value":"1-2"},
              {"connection_type":"application","application_id":"12a"}],
             [], "steam"]
            """);
        // Nothing survives, and no empty group is sent.
        Assert.Empty(groups);
    }

    [Fact]
    public void NoMoreThanTenRequirementsGoToDiscord()
    {
        var many = string.Join(',', Enumerable.Range(0, 7).Select(_ => """{"connection_type":"github"}"""));
        var groups = Read($"[[{many}],[{many}]]");
        Assert.Equal(10, groups.Sum(g => g!.AsArray().Count));
        Assert.Equal(new[] { 7, 3 }, groups.Select(g => g!.AsArray().Count));
    }
}
