using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Onboarding's questions and the Server Guide as Exo passes them to Discord, which fail silently: a question without a
/// title or answers, or an answer giving nothing, reaching Discord half-made; more than Discord's 4 pre-join questions
/// or 5 to-dos; a post-join question kept required; an answer's emoji lost between the shape Discord sends and the one
/// it takes; a dropdown not marked from 13 answers; a channel twice in a list, a to-do without what to do there, or a
/// thumbnail hash carrying anything but a hash.
/// </summary>
public sealed class DiscordOnboardingTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Answer(int i, string extra = "") =>
        $$"""{"id":"{{1100 + i}}","title":"Answer {{i}}","channel_ids":["400000000000000002"],"role_ids":[]{{extra}}}""";

    private static string Question(string id, bool prejoin, string options, string extra = "") =>
        $$"""{"id":"{{id}}","title":"What do you play?","in_onboarding":{{(prejoin ? "true" : "false")}},"required":true,"single_select":false,"options":[{{options}}]{{extra}}}""";

    [Fact]
    public void AQuestionPassesAsDiscordSavesIt()
    {
        var prompts = WebHostBridge.OnboardingPrompts(Parse($"[{Question("1792798090351", true, Answer(1, ""","emoji":{"id":null,"name":"🏆","animated":false}"""))}]"))!;
        var question = prompts[0]!;
        Assert.Equal("1792798090351", (string)question["id"]!);
        Assert.True((bool)question["required"]!);
        Assert.Equal(0, (int)question["type"]!);
        var answer = question["options"]![0]!;
        // Discord sends emoji as an object and takes emoji_name: the emoji survives the change of shape.
        Assert.Equal("🏆", (string)answer["emoji_name"]!);
        Assert.Null(answer["emoji_id"]);
        Assert.Equal("", (string)answer["description"]!);
    }

    [Fact]
    public void APostJoinQuestionIsNeverRequiredAndManyAnswersMakeADropdown()
    {
        var answers = string.Join(',', Enumerable.Range(1, 13).Select(i => Answer(i)));
        var question = WebHostBridge.OnboardingPrompts(Parse($"[{Question("7", false, answers)}]"))![0]!;
        Assert.False((bool)question["required"]!);
        Assert.Equal(1, (int)question["type"]!);
    }

    [Theory]
    // No title, no answers, an answer that gives nothing, an id that isn't one.
    [InlineData("""[{"id":"1","title":" ","in_onboarding":true,"options":[{"id":"2","title":"A","channel_ids":["3"]}]}]""")]
    [InlineData("""[{"id":"1","title":"Q","in_onboarding":true,"options":[]}]""")]
    [InlineData("""[{"id":"1","title":"Q","in_onboarding":true,"options":[{"id":"2","title":"A","channel_ids":[],"role_ids":[]}]}]""")]
    [InlineData("""[{"id":"x1","title":"Q","in_onboarding":true,"options":[{"id":"2","title":"A","channel_ids":["3"]}]}]""")]
    public void AHalfMadeQuestionSendsNothing(string json)
    {
        Assert.Null(WebHostBridge.OnboardingPrompts(Parse(json)));
    }

    [Fact]
    public void NoMoreThanFourPreJoinQuestions()
    {
        var five = string.Join(',', Enumerable.Range(1, 5).Select(i => Question(i.ToString(System.Globalization.CultureInfo.InvariantCulture), true, Answer(i))));
        Assert.Null(WebHostBridge.OnboardingPrompts(Parse($"[{five}]")));
        var four = string.Join(',', Enumerable.Range(1, 4).Select(i => Question(i.ToString(System.Globalization.CultureInfo.InvariantCulture), true, Answer(i))));
        Assert.Equal(4, WebHostBridge.OnboardingPrompts(Parse($"[{four}]"))!.Count);
    }

    private const string Guide = """
        {"welcome_message":{"author_ids":["100000000000000001","2"],"message":"Hi [@username]! Welcome."},"enabled":true,
         "new_member_actions":[{"channel_id":"400000000000000002","action_type":1,"title":"Say hi in general","emoji":{"name":"👋"},"icon":"a_1f2e"}],
         "resource_channels":[{"channel_id":"400000000000000001","title":"Rules","description":"Read first","emoji":null}]}
        """;

    [Fact]
    public void TheServerGuidePassesWhole()
    {
        var guide = WebHostBridge.ServerGuide(Parse(Guide), "300000000000000001")!;
        Assert.Equal("300000000000000001", (string)guide["guild_id"]!);
        // One author, as Discord's page picks one.
        Assert.Single(guide["welcome_message"]!["author_ids"]!.AsArray());
        var task = guide["new_member_actions"]![0]!;
        Assert.Equal(1, (int)task["action_type"]!);
        Assert.Equal("👋", (string)task["emoji"]!["name"]!);
        Assert.Equal("a_1f2e", (string)task["icon"]!);
        Assert.Equal("Read first", (string)guide["resource_channels"]![0]!["description"]!);
        Assert.True((bool)guide["enabled"]!);
    }

    [Theory]
    // A channel twice, a to-do without what to do there, six to-dos, a thumbnail that isn't a hash.
    [InlineData("\"resource_channels\":[{\"channel_id\":\"5\",\"title\":\"A\"},{\"channel_id\":\"5\",\"title\":\"B\"}]")]
    [InlineData("\"new_member_actions\":[{\"channel_id\":\"5\",\"title\":\"Say hi there\"}]")]
    [InlineData("\"new_member_actions\":[{\"channel_id\":\"1\",\"action_type\":0,\"title\":\"Visit one\"},{\"channel_id\":\"2\",\"action_type\":0,\"title\":\"Visit two\"},{\"channel_id\":\"3\",\"action_type\":0,\"title\":\"Visit three\"},{\"channel_id\":\"4\",\"action_type\":0,\"title\":\"Visit four\"},{\"channel_id\":\"5\",\"action_type\":0,\"title\":\"Visit five\"},{\"channel_id\":\"6\",\"action_type\":0,\"title\":\"Visit six\"}]")]
    public void AGuideDiscordWouldRefuseSendsNothing(string part)
    {
        Assert.Null(WebHostBridge.ServerGuide(Parse("{" + part + "}"), "3"));
    }

    [Fact]
    public void AThumbnailThatIsntAHashIsLeftOut()
    {
        var guide = WebHostBridge.ServerGuide(Parse("""{"resource_channels":[{"channel_id":"5","title":"Rules","icon":"../../x"}]}"""), "3")!;
        Assert.Null(guide["resource_channels"]![0]!["icon"]);
    }
}
