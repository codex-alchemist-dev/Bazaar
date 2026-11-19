using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Tests.Gateway.Policy;

/// <summary>Chaos round 3: "$" also matches before a final newline, so a provider id or route name with a trailing newline looked valid.</summary>
public sealed class ModelNameNewlineTests
{
    [Theory]
    [InlineData("custom/cheap\n")]
    [InlineData("route\n")]
    public void A_route_name_with_a_trailing_newline_is_not_valid(string name) => Assert.False(RouteNames.IsValid(name));

    [Theory]
    [InlineData("anthropic-direct\n:model-one")]
    [InlineData("prov\n:model")]
    public void A_provider_id_with_a_trailing_newline_does_not_parse(string text) => Assert.False(ModelRef.TryParse(text, out _));
}
