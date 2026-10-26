using System.Net;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// FreeLLMAPI speaks OpenAI chat, so its calls run through the openai_chat adapter. These tests check that the
/// per-call response-header hook sees the routing headers, including on a failed call, and that the key never
/// reaches an event.
/// </summary>
public sealed class FreeLlmApiChatTests
{
    private static ProviderEndpoint Endpoint(Action<FreeLlmApiRouting> observe) => new(
        FreeLlmApiTestRig.BaseUrl,
        "llama-3.3-70b",
        FreeLlmApiTestRig.TestKey,
        OnResponseHeaders: headers => observe(FreeLlmApiRouting.Parse(headers)));

    [Fact]
    public async Task A_direct_call_streams_text_and_reports_the_routed_model()
    {
        var handler = new StubHandler();
        handler.Reply(FreeLlmApiTestRig.WithHeaders(Replies.Sse(FreeLlmApiTestRig.Fixture("chat_direct.sse")), "chat_direct.headers"));
        FreeLlmApiRouting? routing = null;

        var events = await OpenAiChatTestRig.Run(handler, Endpoint(r => routing = r));

        Assert.Equal("Hi", OpenAiChatTestRig.TextOf(events));
        Assert.Equal("llama-3.3-70b", routing?.RoutedModel);
        Assert.Equal("llama-3.3-70b", events.OfType<ModelEvent.Done>().Single().ServedModel);
    }

    [Fact]
    public async Task A_fallback_call_reports_the_trail_the_upstream_named()
    {
        var handler = new StubHandler();
        handler.Reply(FreeLlmApiTestRig.WithHeaders(Replies.Sse(FreeLlmApiTestRig.Fixture("chat_fallback.sse")), "chat_fallback.headers"));
        FreeLlmApiRouting? routing = null;

        var events = await OpenAiChatTestRig.Run(handler, Endpoint(r => routing = r));

        Assert.Equal("Fallback", OpenAiChatTestRig.TextOf(events));
        Assert.Equal(2, routing?.Attempts);
        Assert.Equal(["llama-3.3-70b", "gemini-2.5-flash"], routing?.TrailModels);
        Assert.Equal("gemini-2.5-flash", events.OfType<ModelEvent.Done>().Single().ServedModel);
    }

    [Fact]
    public async Task The_hook_still_runs_when_the_upstream_rate_limits_the_call()
    {
        var handler = new StubHandler();
        handler.Reply(FreeLlmApiTestRig.WithHeaders(
            Replies.Error(HttpStatusCode.TooManyRequests, FreeLlmApiTestRig.Fixture("unauthorized_401.json")),
            "chat_fallback.headers"));
        FreeLlmApiRouting? routing = null;

        var events = await OpenAiChatTestRig.Run(handler, Endpoint(r => routing = r));

        Assert.NotNull(routing);
        Assert.Equal(["llama-3.3-70b", "gemini-2.5-flash"], routing.TrailModels);
        Assert.IsType<ModelEvent.Failure>(Assert.Single(events));
    }

    [Fact]
    public async Task The_key_is_sent_as_a_bearer_token_and_never_appears_in_an_event()
    {
        var handler = new StubHandler();
        handler.Reply(FreeLlmApiTestRig.WithHeaders(Replies.Sse(FreeLlmApiTestRig.Fixture("chat_direct.sse")), "chat_direct.headers"));

        var events = await OpenAiChatTestRig.Run(handler, Endpoint(_ => { }));

        Assert.Equal($"Bearer {FreeLlmApiTestRig.TestKey}", handler.Requests.Single().Authorization);
        Assert.DoesNotContain(FreeLlmApiTestRig.TestKey, string.Concat(events.Select(e => e.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_goes_to_the_chat_completions_path_under_the_base_url()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(FreeLlmApiTestRig.Fixture("chat_direct.sse")));

        await OpenAiChatTestRig.Run(handler, Endpoint(_ => { }));

        Assert.Equal($"{FreeLlmApiTestRig.BaseUrl}/chat/completions", handler.Requests.Single().Url.ToString());
    }
}
