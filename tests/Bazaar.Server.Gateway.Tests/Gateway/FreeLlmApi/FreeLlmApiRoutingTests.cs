using System.Net;
using Bazaar.Server.Gateway.FreeLlmApi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

public sealed class FreeLlmApiRoutingTests
{
    [Fact]
    public void A_direct_call_names_the_routed_model_and_no_trail()
    {
        var routing = FreeLlmApiRouting.Parse(FreeLlmApiTestRig.ResponseWithHeaders("chat_direct.headers").Headers);

        Assert.Equal("groq", routing.RoutedPlatform);
        Assert.Equal("llama-3.3-70b", routing.RoutedModel);
        Assert.Null(routing.Attempts);
        Assert.Empty(routing.Trail);
        Assert.False(routing.TrailMalformed);
    }

    [Fact]
    public void A_fallback_call_reads_attempts_and_every_failed_hop()
    {
        var routing = FreeLlmApiRouting.Parse(FreeLlmApiTestRig.ResponseWithHeaders("chat_fallback.headers").Headers);

        Assert.Equal("google", routing.RoutedPlatform);
        Assert.Equal("gemini-2.5-flash", routing.RoutedModel);
        Assert.Equal(2, routing.Attempts);
        Assert.Equal(["llama-3.3-70b", "gemini-2.5-flash"], routing.TrailModels);
        Assert.Equal(new FreeLlmApiHop("groq", "llama-3.3-70b", "key1", "rate_limited"), routing.Trail[0]);
        Assert.Equal(new FreeLlmApiHop("google", "gemini-2.5-flash", "key2", "timeout"), routing.Trail[1]);
        Assert.False(routing.TrailMalformed);
    }

    [Fact]
    public void A_hop_that_cannot_be_read_marks_the_trail_malformed_and_keeps_the_good_hops()
    {
        var routing = FreeLlmApiRouting.Parse(FreeLlmApiTestRig.ResponseWithHeaders("chat_trail_malformed.headers").Headers);

        Assert.True(routing.TrailMalformed);
        Assert.Equal(["llama-3.3-70b"], routing.TrailModels);
    }

    [Fact]
    public void Missing_routing_headers_give_no_routed_model_and_no_malformed_flag()
    {
        var routing = FreeLlmApiRouting.Parse(FreeLlmApiTestRig.ResponseWithHeaders("chat_no_routing.headers").Headers);

        Assert.Null(routing.RoutedPlatform);
        Assert.Null(routing.RoutedModel);
        Assert.Empty(routing.Trail);
        Assert.False(routing.TrailMalformed);
    }

    [Fact]
    public void A_model_id_that_contains_a_slash_survives_the_split()
    {
        var (platform, model) = FreeLlmApiRouting.SplitPlatformModel("openrouter/meta-llama/llama-4");

        Assert.Equal("openrouter", platform);
        Assert.Equal("meta-llama/llama-4", model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noslash")]
    [InlineData("/model")]
    [InlineData("platform/")]
    public void Text_without_a_platform_and_model_pair_splits_to_nothing(string? text)
    {
        var (platform, model) = FreeLlmApiRouting.SplitPlatformModel(text);

        Assert.Null(platform);
        Assert.Null(model);
    }

    [Fact]
    public void A_non_numeric_attempts_header_is_ignored_not_thrown()
    {
        var response = FreeLlmApiTestRig.ResponseWithHeaders("chat_direct.headers");
        response.Headers.TryAddWithoutValidation(FreeLlmApiRouting.AttemptsHeader, "two");

        var routing = FreeLlmApiRouting.Parse(response.Headers);

        Assert.Null(routing.Attempts);
    }

    [Fact]
    public void A_trail_hop_with_no_key_or_reason_still_reads_its_model()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation(FreeLlmApiRouting.TrailHeader, "groq/llama-3.3-70b");

        var routing = FreeLlmApiRouting.Parse(response.Headers);

        Assert.False(routing.TrailMalformed);
        Assert.Equal(new FreeLlmApiHop("groq", "llama-3.3-70b", null, null), routing.Trail[0]);
    }
}
