namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

public sealed class ModelBadgeTests
{
    [Theory]
    [InlineData("anthropic-direct:claude-haiku-5-5", "Haiku 5.5")]
    [InlineData("freellmapi:claude-sonnet-5-5", "Sonnet 5.5")]
    [InlineData("claude-OPUS-4-1", "Opus 4.1")]
    public void A_claude_route_gives_its_family_and_version(string route, string label) =>
        Assert.Equal(label, ModelBadge.FromRoutes([route]));

    [Fact]
    public void The_first_route_that_names_a_claude_model_wins()
    {
        Assert.Equal("Sonnet 5.5", ModelBadge.FromRoutes(["claude-first-agent", "custom/x", "anthropic-direct:claude-sonnet-5-5", "claude-haiku-5-5"]));
    }

    [Theory]
    [InlineData("claude-first-agent")]
    [InlineData("custom/local-model")]
    [InlineData("")]
    public void A_route_without_a_claude_model_gives_no_label(string route) => Assert.Null(ModelBadge.FromRoutes([route]));

    [Fact]
    public void No_routes_gives_no_label() => Assert.Null(ModelBadge.FromRoutes([]));
}
