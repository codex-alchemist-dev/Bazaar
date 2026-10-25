using System.Net;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Tests.Gateway.Adapters.Anthropic;

public class AnthropicMessagesErrorTests
{
    private const string RateLimitBody = """{"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit."}}""";
    private const string SpendCapBody = """{"type":"error","error":{"type":"rate_limit_error","message":"Your credit balance is too low to complete this request. Enforced spend limit reached."}}""";

    private static Task<List<ModelEvent>> Run(StubHandler handler) =>
        AnthropicTestSupport.Collect(AnthropicTestSupport.Adapter(handler), AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Http_429_with_retry_after_is_rate_limited_with_the_delay()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(HttpStatusCode.TooManyRequests, RateLimitBody, retryAfter: "30"));

        var events = await Run(handler);

        Assert.Equal(
            [new ModelEvent.Failure("rate_limited", "Number of request tokens has exceeded your per-minute rate limit.", 30)],
            events);
    }

    [Fact]
    public async Task Http_429_without_retry_after_has_no_delay()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(HttpStatusCode.TooManyRequests, RateLimitBody));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("rate_limited", failure.Code);
        Assert.Null(failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task Http_429_with_spend_cap_wording_is_spend_capped_without_a_retry_time()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(HttpStatusCode.TooManyRequests, SpendCapBody, retryAfter: "60"));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("spend_capped", failure.Code);
        Assert.Equal("Your credit balance is too low to complete this request. Enforced spend limit reached.", failure.Message);
        Assert.Null(failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task Http_500_is_a_provider_error_with_the_upstream_message()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(HttpStatusCode.InternalServerError,
            """{"type":"error","error":{"type":"api_error","message":"Internal server error"}}"""));

        Assert.Equal([new ModelEvent.Failure("provider_error", "Internal server error")], await Run(handler));
    }

    [Fact]
    public async Task Http_error_with_a_non_json_body_gets_a_generic_message()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>bad gateway</html>"),
        });

        Assert.Equal([new ModelEvent.Failure("provider_error", "The upstream returned HTTP 502.")], await Run(handler));
    }

    [Fact]
    public async Task Http_401_is_a_provider_error_and_leaks_no_key()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));

        var events = await Run(handler);

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(events));
        Assert.Equal("provider_error", failure.Code);
        Assert.DoesNotContain("test-key", failure.Message);
    }

    [Fact]
    public async Task Stream_error_rate_limit_event_is_rate_limited()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":2,"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("error", """{"type":"error","error":{"type":"rate_limit_error","message":"Rate limited mid-stream."}}""");
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));

        var events = await Run(handler);

        Assert.Equal(new ModelEvent.Failure("rate_limited", "Rate limited mid-stream."), events[^1]);
        Assert.IsType<ModelEvent.UsageReported>(events[^2]);
    }

    [Fact]
    public async Task Timeout_from_the_http_client_is_reported_as_timeout_and_does_not_throw()
    {
        using var handler = new StubHandler(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

        Assert.Equal([new ModelEvent.Failure("timeout", "The upstream did not respond in time.")], await Run(handler));
    }

    [Fact]
    public async Task Connection_failure_is_a_provider_error_and_does_not_leak_the_host()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused (api.example.test:443)."));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("provider_error", failure.Code);
        Assert.DoesNotContain("example.test", failure.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_failure()
    {
        using var handler = new StubHandler(_ => throw new TaskCanceledException("Cancelled by the caller."));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AnthropicTestSupport.Collect(AnthropicTestSupport.Adapter(handler), AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), cts.Token));
    }
}
