using System.Net;
using System.Text;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

public class FreeLlmApiHealthProbeTests
{
    private const string _key = FreeLlmApiTestRig.TestKey;

    private static readonly CancellationToken _ct = CancellationToken.None;

    private static ProviderEndpoint Endpoint(string? apiKey = _key) =>
        new(FreeLlmApiTestRig.BaseUrl, "auto", apiKey);

    private static Func<CancellationToken, Task<HttpResponseMessage>> Body(HttpStatusCode status, string body) => _ =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static async Task<ProbeResult> Probe(StubHandler handler, Func<CancellationToken, Task<HttpResponseMessage>> reply, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        handler.Reply(reply);
        using var http = FreeLlmApiTestRig.Client(handler, timeout);
        return await new FreeLlmApiHealthProbe(http, time).ProbeAsync(Endpoint(), _ct);
    }

    [Fact]
    public async Task A_model_list_with_ready_rows_is_healthy()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Body(HttpStatusCode.OK, FreeLlmApiTestRig.Fixture("models.json")));

        Assert.True(result.Healthy);
        Assert.Equal("HTTP 200, 2 ready", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_model_list_with_no_ready_rows_is_unhealthy()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Body(HttpStatusCode.OK, FreeLlmApiTestRig.Fixture("models_none_ready.json")));

        Assert.False(result.Healthy);
        Assert.Equal("no ready models", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
    }

    [Fact]
    public async Task A_401_is_unhealthy_with_the_status_only()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Body(HttpStatusCode.Unauthorized, FreeLlmApiTestRig.Fixture("unauthorized_401.json")));

        Assert.False(result.Healthy);
        Assert.Equal("HTTP 401", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
        Assert.DoesNotContain("authentication_error", result.Detail);
    }

    [Fact]
    public async Task A_2xx_body_that_is_not_a_model_list_is_malformed()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Body(HttpStatusCode.OK, "not json"));

        Assert.False(result.Healthy);
        Assert.Equal("malformed model list", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_unhealthy_without_throwing()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Replies.Refused());

        Assert.False(result.Healthy);
        Assert.Equal("unreachable", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
    }

    [Fact]
    public async Task A_hung_upstream_times_out_without_throwing()
    {
        var handler = new StubHandler();

        var result = await Probe(handler, Replies.Hang(), timeout: TimeSpan.FromMilliseconds(200));

        Assert.False(result.Healthy);
        Assert.Equal("timeout", result.Detail);
        Assert.DoesNotContain(_key, result.Detail);
    }

    [Fact]
    public async Task The_request_is_a_bearer_get_to_the_available_models_list()
    {
        var handler = new StubHandler();

        await Probe(handler, Body(HttpStatusCode.OK, FreeLlmApiTestRig.Fixture("models.json")));

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/models?available=true", request.Url.ToString());
        Assert.Equal($"Bearer {_key}", request.Authorization);
    }

    [Fact]
    public async Task Latency_comes_from_the_injected_clock_and_is_not_negative()
    {
        var clock = new FakeClock();
        var handler = new StubHandler();
        handler.Reply(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(120));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FreeLlmApiTestRig.Fixture("models.json"), Encoding.UTF8, "application/json"),
            });
        });
        using var http = FreeLlmApiTestRig.Client(handler);

        var result = await new FreeLlmApiHealthProbe(http, clock).ProbeAsync(Endpoint(), _ct);

        Assert.True(result.Healthy);
        Assert.Equal(TimeSpan.FromMilliseconds(120), result.Latency);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task The_caller_cancellation_propagates()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Hang());
        using var http = FreeLlmApiTestRig.Client(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FreeLlmApiHealthProbe(http).ProbeAsync(Endpoint(), cts.Token));
    }
}
