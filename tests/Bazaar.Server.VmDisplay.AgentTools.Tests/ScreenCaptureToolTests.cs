using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools.Tests;

public class ScreenCaptureToolTests
{
    private readonly FakeDisplay _display = new();
    private readonly FakeTargets _targets = Rig.TargetsFor(Rig.Agent);

    private ScreenCaptureTool Tool(ScreenToolsOptions? options = null) => new(_display, _targets, options ?? new ScreenToolsOptions());

    private Task<string> Run(string args, Guid? agent = null, CancellationToken ct = default) =>
        Tool().ExecuteAsync(Rig.Json(args), Rig.Context(agent), ct);

    [Fact]
    public async Task Capture_returns_the_frame_as_base64_with_its_size_and_marks_it_untrusted()
    {
        _display.Frame = () => Rig.Frame(64, 48, bytes: 10, source: DisplaySourceKind.Guest);
        var json = Rig.Parse(await Run("""{"target":"linux"}""", ct: TestContext.Current.CancellationToken));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("untrusted").GetBoolean());
        Assert.Equal("png", json.GetProperty("format").GetString());
        Assert.Equal(64, json.GetProperty("width").GetInt32());
        Assert.Equal(48, json.GetProperty("height").GetInt32());
        Assert.Equal("guest", json.GetProperty("source").GetString());
        Assert.Equal(10, json.GetProperty("bytes").GetInt32());
        Assert.Equal(10, Convert.FromBase64String(json.GetProperty("image_base64").GetString()!).Length);
    }

    [Theory]
    [InlineData("linux", DisplayGuestOs.Linux)]
    [InlineData("windows", DisplayGuestOs.Windows)]
    public async Task The_request_names_the_callers_own_vm_and_no_source_by_default(string target, DisplayGuestOs os)
    {
        await Run($$"""{"target":"{{target}}"}""", ct: TestContext.Current.CancellationToken);

        CaptureRequest request = Assert.Single(_display.Captures);
        Assert.Equal(os, request.Target.Os);
        Assert.Equal(Rig.Agent.ToString("D"), request.Target.AgentId);
        Assert.Null(request.Source);
        Assert.Equal([(Rig.Agent, target == "linux" ? AgentDisplayKind.Linux : AgentDisplayKind.Windows)], _targets.Asked);
    }

    [Fact]
    public async Task The_call_token_reaches_the_asynchronous_target_lookup()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("""{"target":"linux"}""", ct: cts.Token));

        Assert.Empty(_display.Captures);
    }

    [Fact]
    public async Task The_browser_target_uses_the_browser_source_and_session()
    {
        await Run("""{"target":"browser"}""", ct: TestContext.Current.CancellationToken);

        CaptureRequest request = Assert.Single(_display.Captures);
        Assert.Equal(DisplaySourceKind.Browser, request.Source);
        Assert.Equal("session-1", request.Target.BrowserSessionId);
    }

    [Theory]
    [InlineData("hypervisor", DisplaySourceKind.Hypervisor)]
    [InlineData("guest", DisplaySourceKind.Guest)]
    public async Task A_named_source_is_passed_through(string source, DisplaySourceKind expected)
    {
        await Run($$"""{"target":"windows","source":"{{source}}"}""", ct: TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.Single(_display.Captures).Source);
    }

    [Fact]
    public async Task The_target_comes_from_the_calling_agent_so_another_agents_vm_is_never_reached()
    {
        _targets.Map[(Rig.OtherAgent, AgentDisplayKind.Linux)] = Rig.Target(Rig.OtherAgent, DisplayGuestOs.Linux);
        await Run("""{"target":"linux"}""", ct: TestContext.Current.CancellationToken);

        Assert.Equal(Rig.Agent.ToString("D"), Assert.Single(_display.Captures).Target.AgentId);
    }

    [Theory]
    [InlineData("""{"target":"linux","agent_id":"22222222-2222-2222-2222-222222222222"}""")]
    [InlineData("""{"target":"linux","vm_id":"22222222-2222-2222-2222-222222222222"}""")]
    [InlineData("""{"target":"linux","max_width":100}""")]
    public async Task Forged_or_unknown_fields_are_refused(string args)
    {
        var json = Rig.Parse(await Run(args, ct: TestContext.Current.CancellationToken));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_arguments", json.GetProperty("status").GetString());
        Assert.Contains("unknown field", json.GetProperty("message").GetString());
        Assert.Empty(_display.Captures);
        Assert.Empty(_targets.Asked);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"target":"mac"}""")]
    [InlineData("""{"target":5}""")]
    [InlineData("""{"target":"linux","source":"magic"}""")]
    [InlineData("""{"target":"linux","source":"browser"}""")]
    [InlineData("""{"target":"browser","source":"guest"}""")]
    [InlineData("""[1]""")]
    public async Task Bad_arguments_are_refused_before_any_display_call(string args)
    {
        var json = Rig.Parse(await Run(args, ct: TestContext.Current.CancellationToken));

        Assert.Equal("invalid_arguments", json.GetProperty("status").GetString());
        Assert.Empty(_display.Captures);
    }

    [Fact]
    public async Task An_agent_without_the_target_gets_no_target()
    {
        var json = Rig.Parse(await Run("""{"target":"linux"}""", Rig.OtherAgent, TestContext.Current.CancellationToken));

        Assert.Equal("no_target", json.GetProperty("status").GetString());
        Assert.Empty(_display.Captures);
    }

    [Fact]
    public async Task A_resolver_returning_another_agents_target_is_refused()
    {
        _targets.Map[(Rig.Agent, AgentDisplayKind.Linux)] = Rig.Target(Rig.OtherAgent, DisplayGuestOs.Linux);
        var json = Rig.Parse(await Run("""{"target":"linux"}""", ct: TestContext.Current.CancellationToken));

        Assert.Equal("no_target", json.GetProperty("status").GetString());
        Assert.Empty(_display.Captures);
    }

    [Fact]
    public async Task A_browser_target_without_a_session_is_refused()
    {
        _targets.Map[(Rig.Agent, AgentDisplayKind.Browser)] = Rig.Target(Rig.Agent, DisplayGuestOs.Linux);
        var json = Rig.Parse(await Run("""{"target":"browser"}""", ct: TestContext.Current.CancellationToken));

        Assert.Equal("no_target", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(DisplayFailure.Disabled, "disabled")]
    [InlineData(DisplayFailure.Refused, "refused")]
    [InlineData(DisplayFailure.RateLimited, "rate_limited")]
    [InlineData(DisplayFailure.NoSourceAvailable, "no_source_available")]
    [InlineData(DisplayFailure.SourceFailed, "source_failed")]
    [InlineData(DisplayFailure.FrameTooLarge, "frame_too_large")]
    [InlineData(DisplayFailure.FrameMalformed, "frame_malformed")]
    [InlineData(DisplayFailure.InvalidInput, "invalid_input")]
    public async Task Each_display_failure_becomes_a_tool_error_without_the_exception_text(DisplayFailure failure, string name)
    {
        _display.Fail = new DisplayException(failure, "secret host detail C:\\vm\\disk.vhdx");
        string result = await Run("""{"target":"linux"}""", ct: TestContext.Current.CancellationToken);
        var json = Rig.Parse(result);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("display_failed", json.GetProperty("status").GetString());
        Assert.Equal(name, json.GetProperty("failure").GetString());
        Assert.DoesNotContain("vhdx", result);
    }

    [Fact]
    public async Task A_frame_over_the_cap_is_refused_with_its_size_and_no_image()
    {
        _display.Frame = () => Rig.Frame(1920, 1080, bytes: 2000);
        string result = await Tool(new ScreenToolsOptions { MaxImageBytes = 1000 })
            .ExecuteAsync(Rig.Json("""{"target":"linux"}"""), Rig.Context(), TestContext.Current.CancellationToken);
        var json = Rig.Parse(result);

        Assert.Equal("image_too_large", json.GetProperty("status").GetString());
        Assert.Equal(2000, json.GetProperty("bytes").GetInt32());
        Assert.False(json.TryGetProperty("image_base64", out _));
    }

    [Fact]
    public async Task A_frame_exactly_at_the_cap_is_returned()
    {
        _display.Frame = () => Rig.Frame(8, 8, bytes: 1000);
        string result = await Tool(new ScreenToolsOptions { MaxImageBytes = 1000 })
            .ExecuteAsync(Rig.Json("""{"target":"linux"}"""), Rig.Context(), TestContext.Current.CancellationToken);

        Assert.True(Rig.Parse(result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Cancellation_flows_through()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("""{"target":"linux"}""", ct: cts.Token));
    }

    [Fact]
    public void The_definition_names_the_tool_states_untrusted_and_forbids_extra_fields()
    {
        Assert.Equal("screen.capture", ScreenCaptureTool.Definition.Name);
        Assert.Contains("UNTRUSTED", ScreenCaptureTool.Definition.Description);
        Assert.Contains("\"additionalProperties\": false", ScreenCaptureTool.Definition.ParametersJsonSchema);
        Rig.Parse(ScreenCaptureTool.Definition.ParametersJsonSchema);
    }

    [Fact]
    public async Task A_plain_call_without_the_dispatcher_fails_loudly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((Bazaar.Server.Agents.Turns.Tools.ITool)Tool()).ExecuteAsync(Rig.Json("""{"target":"linux"}"""), TestContext.Current.CancellationToken));
    }
}
