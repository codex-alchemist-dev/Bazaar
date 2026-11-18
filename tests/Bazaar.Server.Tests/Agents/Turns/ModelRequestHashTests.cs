using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The request hash over a model call. A blank route is valid under per-agent routing (doc 05).</summary>
public sealed class ModelRequestHashTests
{
    private static readonly ModelMessage[] _messages = [new ModelMessage(ModelRole.User, "hello")];

    [Fact]
    public void A_blank_route_hashes_without_throwing_and_stays_distinct_from_a_named_route()
    {
        var blank = ModelRequestHash.Compute(string.Empty, _messages, []);
        var named = ModelRequestHash.Compute("claude-haiku-5-5", _messages, []);

        Assert.StartsWith("sha256:", blank, StringComparison.Ordinal);
        Assert.NotEqual(blank, named);
        Assert.Equal(blank, ModelRequestHash.Compute(string.Empty, _messages, []));
    }

    [Fact]
    public void A_null_route_is_still_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ModelRequestHash.Compute(null!, _messages, []));
    }
}
