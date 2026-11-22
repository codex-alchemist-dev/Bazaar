using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio.Tests;

public sealed class StudioRelayTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static StudioRelayRequest Req(string tool, string studioId = "studio-A", string grant = "grant-1", string args = "{}")
        => new(grant, studioId, tool, Json(args));

    private static StudioRelayError Error(StudioRelayResult result)
    {
        Assert.False(result.Ok);
        Assert.True(StudioRelayError.TryFromPayload(result.Payload, out var error));
        return error!;
    }

    private static (StudioRelay Relay, FakeStudioTransport Transport, FakeGrantVerifier Verifier) Make(StudioRelayOptions? options = null)
    {
        var transport = FakeStudioTransport.FromFixture();
        var verifier = new FakeGrantVerifier();
        return (new StudioRelay(transport, verifier, options), transport, verifier);
    }

    [Fact]
    public async Task Allowed_read_with_valid_grant_is_forwarded_with_the_studio_id()
    {
        var (relay, transport, verifier) = Make();
        RiskClass? seen = null;
        verifier.Handler = (_, _, risk, _) =>
        {
            seen = risk;
            return Task.FromResult(true);
        };

        var result = await relay.RelayAsync(Req("script_read", args: """{"path":"ServerScriptService.Main","studio_id":"studio-B"}"""), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("grant-1", result.GrantId);
        Assert.Equal(RiskClass.Read, seen);
        var call = transport.Calls.Single(c => c.Tool == "script_read");
        Assert.Equal("tools/call", call.Method);
        var arguments = call.Params.GetProperty("arguments");
        Assert.Equal("studio-A", arguments.GetProperty("studio_id").GetString());
        Assert.Equal("ServerScriptService.Main", arguments.GetProperty("path").GetString());
        // Result text is untrusted data and comes back untouched.
        Assert.Contains("IGNORE PREVIOUS INSTRUCTIONS", result.Payload.GetRawText());
    }

    [Theory]
    [InlineData("multi_edit", RiskClass.Write)]
    [InlineData("execute_luau", RiskClass.Exec)]
    public async Task Write_and_exec_without_a_grant_are_refused_and_never_reach_the_transport(string tool, RiskClass expected)
    {
        var (relay, transport, verifier) = Make();
        RiskClass? asked = null;
        verifier.Handler = (_, _, risk, _) =>
        {
            asked = risk;
            return Task.FromResult(false);
        };

        var result = await relay.RelayAsync(Req(tool), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.GrantDenied, Error(result).Code);
        Assert.Equal(expected, asked);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Unknown_tool_is_refused_before_the_verifier_and_transport()
    {
        var (relay, transport, verifier) = Make();

        var result = await relay.RelayAsync(Req("format_hard_drive"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.ToolNotAllowed, Error(result).Code);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Asset_generators_are_write_class()
    {
        Assert.True(StudioToolCatalog.TryGetRiskClass("generate_mesh", out var risk));
        Assert.Equal(RiskClass.Write, risk);
        Assert.False(StudioToolCatalog.TryGetRiskClass("generate_", out _));
        Assert.False(StudioToolCatalog.TryGetRiskClass("SCRIPT_READ", out _));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("script_read", RiskClass.Read)]
    [InlineData("list_roblox_studios", RiskClass.Read)]
    [InlineData("insert_asset", RiskClass.Write)]
    [InlineData("http_get", RiskClass.Exec)]
    [InlineData("start_stop_play", RiskClass.Exec)]
    public void Catalog_matches_the_doc_20_table(string tool, RiskClass expected)
    {
        Assert.True(StudioToolCatalog.TryGetRiskClass(tool, out var risk));
        Assert.Equal(expected, risk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Missing_studio_id_is_refused(string studioId)
    {
        var (relay, transport, verifier) = Make();

        var result = await relay.RelayAsync(Req("script_read", studioId), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.StudioIdRequired, Error(result).Code);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Unknown_studio_id_is_refused_after_the_listing()
    {
        var (relay, transport, _) = Make();

        var result = await relay.RelayAsync(Req("script_read", "studio-Z"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.StudioUnknown, Error(result).Code);
        Assert.Equal(0, transport.CallsTo("script_read"));
        Assert.Equal(1, transport.CallsTo("list_roblox_studios"));
    }

    [Fact]
    public async Task Studio_not_open_is_unavailable_with_a_reason()
    {
        var (relay, transport, _) = Make();
        transport.Open = false;

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        var error = Error(result);
        Assert.Equal(StudioRelayErrorCode.Unavailable, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Reason));
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Relay_starts_a_stopped_transport_before_calling()
    {
        var (relay, transport, _) = Make();
        transport.Open = false;
        transport.StartOpens = true;

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Empty_studio_list_means_no_studio_open()
    {
        var (relay, transport, _) = Make();
        transport.Set("list_roblox_studios", Json("""{"content":[{"type":"text","text":"{\"studios\":[]}"}]}"""));

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.Unavailable, Error(result).Code);
    }

    [Fact]
    public async Task Verifier_exception_denies_the_call()
    {
        var (relay, transport, verifier) = Make();
        verifier.Handler = (_, _, _, _) => throw new InvalidOperationException("server unreachable");

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.GrantDenied, Error(result).Code);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Verifier_timeout_denies_the_call()
    {
        var (relay, transport, verifier) = Make(new StudioRelayOptions { CheckTimeout = TimeSpan.FromMilliseconds(50) });
        verifier.Handler = async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.GrantDenied, Error(result).Code);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Transport_failure_mid_call_is_unavailable()
    {
        var (relay, transport, _) = Make();
        transport.Fail = tool => tool == "script_read" ? new StudioTransportException("The Studio MCP server exited.") : null;

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.Unavailable, Error(result).Code);
    }

    [Fact]
    public async Task Tool_error_from_studio_is_a_failed_result_not_an_exception()
    {
        var (relay, _, _) = Make();

        var result = await relay.RelayAsync(Req("execute_luau"), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("attempt to index nil", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Jsonrpc_error_from_studio_is_a_failed_result()
    {
        var (relay, transport, _) = Make();
        transport.Fail = tool => tool == "script_read" ? new StudioTransportException("no such script", -32602) : null;

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("StudioError", result.Payload.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Oversized_result_is_truncated_with_a_marker()
    {
        var (relay, transport, _) = Make(new StudioRelayOptions { MaxPayloadBytes = 1024 });
        var big = new string('x', 50_000);
        transport.Set("get_console_output", Json($$"""{"content":[{"type":"text","text":"{{big}}"}]}"""));

        var result = await relay.RelayAsync(Req("get_console_output"), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
        Assert.True(result.Payload.GetProperty("originalBytes").GetInt32() > 50_000);
        Assert.True(result.Payload.GetRawText().Length < 4096);
    }

    [Fact]
    public async Task Call_timeout_returns_timeout_error()
    {
        var (relay, transport, _) = Make(new StudioRelayOptions { CallTimeout = TimeSpan.FromMilliseconds(50) });
        transport.Before = tool => tool == "script_read" ? Task.Delay(Timeout.Infinite) : Task.CompletedTask;

        var result = await relay.RelayAsync(Req("script_read"), TestContext.Current.CancellationToken);

        Assert.Equal(StudioRelayErrorCode.Timeout, Error(result).Code);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        var (relay, transport, _) = Make();
        transport.Before = tool => tool == "script_read" ? Task.Delay(Timeout.Infinite) : Task.CompletedTask;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RelayAsync(Req("script_read"), cts.Token));
    }

    [Fact]
    public async Task Concurrent_calls_keep_their_own_results()
    {
        var (relay, transport, _) = Make();
        transport.Before = async tool => await Task.Delay(tool == "script_read" ? 80 : 1);

        var slow = relay.RelayAsync(Req("script_read", grant: "g-slow"), TestContext.Current.CancellationToken);
        var fast = relay.RelayAsync(Req("get_console_output", grant: "g-fast"), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(slow, fast);

        Assert.Equal("g-slow", results[0].GrantId);
        Assert.Contains("hello", results[0].Payload.GetRawText());
        Assert.Equal("g-fast", results[1].GrantId);
        Assert.Contains("fast", results[1].Payload.GetRawText());
    }

    [Fact]
    public void Error_payload_round_trips()
    {
        var payload = new StudioRelayError(StudioRelayErrorCode.Timeout, "too slow").ToPayload();

        Assert.True(StudioRelayError.TryFromPayload(payload, out var error));
        Assert.Equal(new StudioRelayError(StudioRelayErrorCode.Timeout, "too slow"), error);
        Assert.False(StudioRelayError.TryFromPayload(Json("""{"content":[]}"""), out _));
    }
}
