using System.Net;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>Every call is audited, and no secret or bootstrap code reaches a response body, a log line or an audit row.</summary>
public sealed class AuditAndSecretTests
{
    private static JsonElement PayloadOf(AuditEntry entry) => SetupHarness.Parse(entry.Payload);

    [Fact]
    public async Task Every_call_is_audited_including_refusals_and_rate_limited_calls()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var refused = await harness.SendAsync(HttpMethod.Get, "/setup/status");
        using var admitted = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));
        harness.Limiter.Deny = true;
        using var limited = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));

        var rows = harness.RowsOf(SetupHttpConstants.AuditKind);
        Assert.Equal(3, rows.Count);

        Assert.Equal(SetupHttpConstants.RefusedActor, rows[0].Actor);
        Assert.Equal(401, PayloadOf(rows[0]).GetProperty("status").GetInt32());
        Assert.Equal("refused:bootstrap_code_refused", PayloadOf(rows[0]).GetProperty("outcome").GetString());
        Assert.Equal("none", PayloadOf(rows[0]).GetProperty("caller").GetString());

        Assert.Equal(SetupHttpConstants.BootstrapActor, rows[1].Actor);
        Assert.Equal(200, PayloadOf(rows[1]).GetProperty("status").GetInt32());
        Assert.Equal("ok", PayloadOf(rows[1]).GetProperty("outcome").GetString());
        Assert.Equal("bootstrap", PayloadOf(rows[1]).GetProperty("caller").GetString());

        Assert.Equal(429, PayloadOf(rows[2]).GetProperty("status").GetInt32());
        Assert.Equal("refused:rate_limited", PayloadOf(rows[2]).GetProperty("outcome").GetString());
        Assert.Equal("status", PayloadOf(rows[2]).GetProperty("route").GetString());
        Assert.Equal("GET", PayloadOf(rows[2]).GetProperty("method").GetString());
    }

    [Fact]
    public async Task A_rate_limited_call_answers_429_with_a_retry_time()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        harness.Limiter.Deny = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter!.Delta);
        Assert.Equal(0, harness.Step.ApplyCalls);
    }

    [Fact]
    public async Task A_failed_audit_write_is_not_answered_as_success()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        harness.Audit.Fail = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("{\"error\":\"unexpected\"}", await SetupHarness.TextOf(response));
    }

    [Fact]
    public async Task The_bootstrap_code_never_reaches_a_response_an_audit_row_or_a_log_line()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var ok = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));
        using var refused = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));

        var seen = new List<string> { await SetupHarness.TextOf(ok), await SetupHarness.TextOf(refused) };
        seen.AddRange(harness.Audit.Entries.Select(entry => entry.Actor + entry.Kind + entry.Payload));
        seen.AddRange(harness.Logs.Lines);
        Assert.All(seen, text => Assert.DoesNotContain(code, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_canary_in_a_secret_typed_key_never_reaches_a_response_an_audit_row_or_a_log_line()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var canaryPlan = PlanFixtures.With(
            "\"keyRef\": \"secret://bazaar/providers/freellmapi/key\"",
            "\"keyRef\": \"" + PlanFixtures.Canary + "\"");
        var owner = new Caller(Token: SetupHarness.OwnerToken);

        using var validate = await harness.SendAsync(HttpMethod.Post, "/setup/plans", owner, canaryPlan);
        using var apply = await harness.SendAsync(HttpMethod.Post, "/setup/plans/" + new string('0', 64) + "/apply", owner, canaryPlan);
        using var status = await harness.SendAsync(HttpMethod.Get, "/setup/status", owner);

        Assert.Equal(HttpStatusCode.BadRequest, validate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(0, harness.Step.ApplyCalls);

        var seen = new List<string>
        {
            await SetupHarness.TextOf(validate),
            await SetupHarness.TextOf(apply),
            await SetupHarness.TextOf(status),
        };
        seen.AddRange(harness.Audit.Entries.Select(entry => entry.Actor + entry.Kind + entry.Payload));
        seen.AddRange(harness.Logs.Lines);
        Assert.All(seen, text => Assert.DoesNotContain(PlanFixtures.Canary, text, StringComparison.Ordinal));
    }
}
