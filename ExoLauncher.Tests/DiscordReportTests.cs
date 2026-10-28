using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a report can go wrong without anyone seeing it (Discord answers a bare 400 and the report is lost):
/// - the ids a kind needs are missing, or sent under the room's names instead of Discord's;
/// - an id that isn't a snowflake, or a kind Discord doesn't have, goes into the URL;
/// - the steps or answers lose their shape (numbers as text, one answer instead of a list).
/// </summary>
public sealed class DiscordReportTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AMessageReportCarriesWhatDiscordAsksFor()
    {
        var report = DiscordReports.Build("message", Params("""
            {"version": "1.0", "variant": "6", "language": "en", "breadcrumbs": [3, 28, 76],
             "elements": {"other_details": ["spam links"]}, "ids": {"channel_id": "123", "message_id": "456", "guild_id": "789"}}
            """))!;
        Assert.Equal("message", report["name"]!.GetValue<string>());
        Assert.Equal([3L, 28L, 76L], report["breadcrumbs"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal("spam links", report["elements"]!["other_details"]![0]!.GetValue<string>());
        Assert.Equal("123", report["channel_id"]!.GetValue<string>());
        Assert.Equal("456", report["message_id"]!.GetValue<string>());
        Assert.Null(report["guild_id"]); // a message report doesn't name the server
    }

    [Fact]
    public void APersonIsReportedUnderDiscordsName()
    {
        var report = DiscordReports.Build("user", Params("""{"version": "1.0", "variant": "3", "breadcrumbs": [1], "ids": {"reported_user_id": "42"}}"""))!;
        Assert.Equal("42", report["reported_user_id"]!.GetValue<string>());
        Assert.Equal("en", report["language"]!.GetValue<string>());
        Assert.Empty(report["elements"]!.AsObject());
    }

    [Theory]
    [InlineData("message", """{"version": "1.0", "variant": "6", "breadcrumbs": [1], "ids": {"channel_id": "123"}}""")]
    [InlineData("message", """{"version": "1.0", "variant": "6", "breadcrumbs": [1], "ids": {"channel_id": "123", "message_id": "../users/@me"}}""")]
    [InlineData("user", """{"version": "1.0", "variant": "3", "breadcrumbs": ["1"], "ids": {"reported_user_id": "42"}}""")]
    [InlineData("user", """{"version": "1.0", "variant": "3", "breadcrumbs": [], "ids": {"reported_user_id": "42"}}""")]
    [InlineData("user", """{"version": "1.0", "variant": "3", "breadcrumbs": [1], "elements": {"reason": "spam"}, "ids": {"reported_user_id": "42"}}""")]
    [InlineData("user", """{"variant": "3", "breadcrumbs": [1], "ids": {"reported_user_id": "42"}}""")]
    [InlineData("../users/@me", """{"version": "1.0", "variant": "3", "breadcrumbs": [1], "ids": {}}""")]
    public void ANotQuiteRightReportIsntSent(string kind, string json) =>
        Assert.Null(DiscordReports.Build(kind, Params(json)));
}
