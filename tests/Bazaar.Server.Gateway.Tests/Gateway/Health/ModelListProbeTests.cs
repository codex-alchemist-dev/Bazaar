using System.Net;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Health;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Health;

public class ModelListProbeTests
{
    private const string _credential = "PROBE-CREDENTIAL-SENTINEL";

    private static readonly CancellationToken _ct = CancellationToken.None;

    private static ProviderEndpoint Endpoint(string? apiKey = _credential, IReadOnlyDictionary<string, string>? extraHeaders = null) =>
        new("https://provider.test/v1/", "model-x", apiKey, extraHeaders);

    /// <summary>A handler that records the last request and plays one scripted reply.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public Uri? LastUrl { get; private set; }

        public string? LastAuthorization { get; private set; }

        public string? LastExtraHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastExtraHeader = request.Headers.TryGetValues("x-extra", out var values) ? string.Join(",", values) : null;
            return reply(request, cancellationToken);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode status) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status));

    [Fact]
    public async Task A_2xx_answer_is_healthy_and_uses_a_bearer_list_call()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        var result = await new ModelListProbe(http).ProbeAsync(Endpoint(), _ct);

        Assert.True(result.Healthy);
        Assert.Equal("HTTP 200", result.Detail);
        Assert.Equal("https://provider.test/v1/models", handler.LastUrl!.ToString());
        Assert.Equal($"Bearer {_credential}", handler.LastAuthorization);
    }

    [Fact]
    public async Task Any_2xx_status_is_healthy()
    {
        using var http = new HttpClient(new ScriptedHandler(Status(HttpStatusCode.NoContent)));

        var result = await new ModelListProbe(http).ProbeAsync(Endpoint(), _ct);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task A_500_is_unhealthy_and_the_detail_has_no_secret()
    {
        using var http = new HttpClient(new ScriptedHandler(Status(HttpStatusCode.InternalServerError)));

        var result = await new ModelListProbe(http).ProbeAsync(Endpoint(), _ct);

        Assert.False(result.Healthy);
        Assert.Equal("HTTP 500", result.Detail);
        Assert.DoesNotContain(_credential, result.Detail);
    }

    [Fact]
    public async Task A_timeout_is_unhealthy_without_throwing()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));
        using var http = new HttpClient(handler);

        var result = await new ModelListProbe(http).ProbeAsync(Endpoint(), _ct);

        Assert.False(result.Healthy);
        Assert.Equal("timeout", result.Detail);
    }

    [Fact]
    public async Task A_transport_exception_is_unhealthy_without_throwing()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("refused")));
        using var http = new HttpClient(handler);

        var result = await new ModelListProbe(http).ProbeAsync(Endpoint(), _ct);

        Assert.False(result.Healthy);
        Assert.Equal("unreachable", result.Detail);
    }

    [Fact]
    public async Task The_latency_is_measured_on_the_injected_clock()
    {
        var clock = new FakeClock();
        var handler = new ScriptedHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler);

        var result = await new ModelListProbe(http, clock).ProbeAsync(Endpoint(), _ct);

        Assert.Equal(TimeSpan.FromMilliseconds(250), result.Latency);
    }

    [Fact]
    public async Task The_caller_cancellation_propagates()
    {
        var handler = new ScriptedHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelListProbe(http).ProbeAsync(Endpoint(), cts.Token));
    }

    [Fact]
    public async Task Extra_headers_are_sent_and_can_override_the_bearer_token()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var extra = new Dictionary<string, string> { ["x-extra"] = "one" };

        await new ModelListProbe(http).ProbeAsync(Endpoint(extraHeaders: extra), _ct);
        Assert.Equal("one", handler.LastExtraHeader);
        Assert.Equal($"Bearer {_credential}", handler.LastAuthorization);

        var overriding = new Dictionary<string, string> { ["Authorization"] = "Custom token-abc" };
        await new ModelListProbe(http).ProbeAsync(Endpoint(extraHeaders: overriding), _ct);
        Assert.Equal("Custom token-abc", handler.LastAuthorization);
    }

    [Fact]
    public async Task Without_a_key_no_authorization_header_is_sent()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await new ModelListProbe(http).ProbeAsync(Endpoint(apiKey: null), _ct);

        Assert.Null(handler.LastAuthorization);
    }
}
