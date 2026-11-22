using System.Net;
using System.Text.Json;
using Bazaar.Conduit.Setup;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.Setup;

public sealed class RemoteReachabilityCheckTests
{
    private static SetupVerificationContext Plan(params (string Key, string Json)[] settings) =>
        new(new SetupPlan { Settings = settings.ToDictionary(s => s.Key, s => JsonElement.Parse(s.Json)) });

    private static async Task<SetupCheckResult> RunAsync(
        SetupVerificationContext context, Func<Uri, CancellationToken, Task<HttpStatusCode?>> fetch, FakeTimeProvider? time = null)
    {
        var clock = time ?? new FakeTimeProvider();
        var run = new RemoteReachabilityCheck(fetch, clock).RunAsync(context, TestContext.Current.CancellationToken);
        while (!run.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        return await run;
    }

    private static readonly Func<Uri, CancellationToken, Task<HttpStatusCode?>> _neverCalled =
        (_, _) => throw new InvalidOperationException("The check must not make a request here.");

    [Fact]
    public async Task Off_is_green_and_makes_no_request()
    {
        var result = await RunAsync(Plan(), _neverCalled);

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task A_quick_tunnel_is_not_evaluated_and_makes_no_request()
    {
        var result = await RunAsync(Plan(("conduit.cloudflared.enabled", "true"), ("conduit.cloudflared.mode", "\"quick\"")), _neverCalled);

        Assert.Equal(SetupCheckStatus.NotEvaluated, result.Status);
    }

    [Fact]
    public async Task A_named_tunnel_without_a_host_name_is_amber()
    {
        var result = await RunAsync(Plan(("conduit.cloudflared.enabled", "true")), _neverCalled);

        Assert.Equal(SetupCheckStatus.Amber, result.Status);
        Assert.Contains("hostname", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_tunnel_that_answers_after_a_minute_is_green()
    {
        var time = new FakeTimeProvider();
        var ready = time.GetUtcNow() + TimeSpan.FromSeconds(65);
        Uri? asked = null;

        var result = await RunAsync(
            Plan(("conduit.cloudflared.enabled", "true"), ("conduit.cloudflared.hostname", "\"bazaar.example.com\"")),
            (uri, _) =>
            {
                asked = uri;
                return Task.FromResult<HttpStatusCode?>(time.GetUtcNow() >= ready ? HttpStatusCode.BadRequest : HttpStatusCode.BadGateway);
            },
            time);

        Assert.Equal(SetupCheckStatus.Green, result.Status);
        Assert.Equal("https://bazaar.example.com/", asked?.ToString());
    }

    [Fact]
    public async Task A_tunnel_that_never_answers_is_amber_not_red()
    {
        var result = await RunAsync(
            Plan(("conduit.cloudflared.enabled", "true"), ("conduit.cloudflared.hostname", "\"bazaar.example.com\"")),
            (_, _) => Task.FromResult<HttpStatusCode?>(null));

        Assert.Equal(SetupCheckStatus.Amber, result.Status);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task Cloudflare_530_means_the_tunnel_is_not_connected()
    {
        var result = await RunAsync(
            Plan(("conduit.cloudflared.enabled", "true"), ("conduit.cloudflared.hostname", "\"bazaar.example.com\"")),
            (_, _) => Task.FromResult<HttpStatusCode?>((HttpStatusCode)530));

        Assert.Equal(SetupCheckStatus.Amber, result.Status);
    }

    [Fact]
    public void The_check_answers_the_placeholder_id_in_the_setup_list()
    {
        Assert.Contains(SetupVerificationChecks.Infrastructure, check => check.Id == RemoteReachabilityCheck.CheckId);
    }
}
