using System.Text;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools.Tests;

public class ScreenInputToolTests
{
    private readonly FakeDisplay _display = new();
    private readonly FakeTargets _targets = Rig.TargetsFor(Rig.Agent);

    private ScreenInputTool Tool(ScreenToolsOptions? options = null, IDisplayNativeSize? native = null) =>
        new(_display, _targets, options ?? new ScreenToolsOptions(), native);

    private static string Args(string actions, string target = "linux", int w = 1000, int h = 500, string extra = "") =>
        $$"""{"target":"{{target}}","frame_width":{{w}},"frame_height":{{h}},{{extra}}"actions":[{{actions}}]}""";

    private async Task<(System.Text.Json.JsonElement Json, string Raw)> Run(string args, ScreenToolsOptions? options = null, IDisplayNativeSize? native = null)
    {
        string raw = await Tool(options, native).ExecuteAsync(Rig.Json(args), Rig.Context(), TestContext.Current.CancellationToken);
        return (Rig.Parse(raw), raw);
    }

    private async Task<IReadOnlyList<InputAction>> Sent(string actions, IDisplayNativeSize? native = null, string target = "linux", int w = 1000, int h = 500)
    {
        var (json, raw) = await Run(Args(actions, target, w, h), native: native);
        Assert.True(json.GetProperty("ok").GetBoolean(), raw);
        return Assert.Single(_display.Inputs).Actions;
    }

    private async Task AssertRefused(string args, string fragment, ScreenToolsOptions? options = null)
    {
        var (json, _) = await Run(args, options);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_arguments", json.GetProperty("status").GetString());
        Assert.Contains(fragment, json.GetProperty("message").GetString());
        Assert.Empty(_display.Inputs);
    }

    [Fact]
    public async Task Every_action_type_maps_to_the_exact_request()
    {
        var actions = await Sent("""
            {"type":"type_text","text":"hello world"},
            {"type":"key","key":"KeyA","modifiers":["control","shift"]},
            {"type":"key","key":"Enter"},
            {"type":"move","x":10,"y":20},
            {"type":"click","x":30,"y":40},
            {"type":"click","x":50,"y":60,"button":"right","count":2},
            {"type":"button_down","button":"middle"},
            {"type":"button_up"},
            {"type":"scroll","x":70,"y":80,"delta_y":-3}
            """);

        Assert.Equal(
            [
                new TypeText("hello world"),
                new KeyStroke("KeyA", KeyModifiers.Control | KeyModifiers.Shift),
                new KeyStroke("Enter", KeyModifiers.None),
                new PointerMove(10, 20),
                new PointerClick(30, 40, PointerButtonKind.Left, 1),
                new PointerClick(50, 60, PointerButtonKind.Right, 2),
                new PointerButton(PointerButtonKind.Middle, true),
                new PointerButton(PointerButtonKind.Left, false),
                new PointerScroll(70, 80, 0, -3),
            ],
            actions);
    }

    [Fact]
    public async Task The_batch_names_the_callers_own_target_and_source()
    {
        var (json, _) = await Run(Args("""{"type":"key","key":"Tab"}""", target: "windows", extra: "\"source\":\"guest\","));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.GetProperty("actions").GetInt32());
        InputBatch batch = Assert.Single(_display.Inputs);
        Assert.Equal(DisplayGuestOs.Windows, batch.Target.Os);
        Assert.Equal(Rig.Agent.ToString("D"), batch.Target.AgentId);
        Assert.Equal(DisplaySourceKind.Guest, batch.Source);
    }

    [Fact]
    public async Task The_browser_target_sends_to_the_browser_source()
    {
        await Run(Args("""{"type":"key","key":"Tab"}""", target: "browser"));
        InputBatch batch = Assert.Single(_display.Inputs);
        Assert.Equal(DisplaySourceKind.Browser, batch.Source);
        Assert.Equal("session-1", batch.Target.BrowserSessionId);
    }

    [Theory]
    [InlineData("""{"target":"linux","vm_id":"x","frame_width":10,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""")]
    [InlineData("""{"target":"linux","agent_id":"x","frame_width":10,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""")]
    public async Task A_forged_id_is_refused_as_an_unknown_field(string args)
    {
        await AssertRefused(args, "unknown field");
        Assert.Empty(_targets.Asked);
    }

    [Fact]
    public async Task Unknown_fields_inside_actions_are_refused()
    {
        await AssertRefused(Args("""{"type":"key","key":"Tab","x":1}"""), "unknown field");
        await AssertRefused(Args("""{"type":"type_text","text":"hi","key":"Tab"}"""), "unknown field");
        await AssertRefused(Args("""{"type":"move","x":1,"y":1,"button":"left"}"""), "unknown field");
    }

    [Theory]
    [InlineData("""{"type":"dance"}""", "known action type")]
    [InlineData("""{"x":1}""", "type is required")]
    [InlineData("""{"type":"key","key":"Hyper"}""", "known key")]
    [InlineData("""{"type":"key"}""", "key is required")]
    [InlineData("""{"type":"key","key":"Tab","modifiers":["hyper"]}""", "unknown modifier")]
    [InlineData("""{"type":"key","key":"Tab","modifiers":"shift"}""", "modifiers")]
    [InlineData("""{"type":"key","key":"Tab","modifiers":["shift","shift","shift","shift","shift"]}""", "modifiers")]
    [InlineData("""{"type":"move","x":-1,"y":0}""", "x must be")]
    [InlineData("""{"type":"move","x":1000,"y":0}""", "x must be")]
    [InlineData("""{"type":"move","x":0,"y":500}""", "y must be")]
    [InlineData("""{"type":"move","x":1.5,"y":0}""", "x must be an integer")]
    [InlineData("""{"type":"move","x":"1","y":0}""", "x must be an integer")]
    [InlineData("""{"type":"move","y":0}""", "x is required")]
    [InlineData("""{"type":"click","x":1,"y":1,"count":0}""", "count must be")]
    [InlineData("""{"type":"click","x":1,"y":1,"count":4}""", "count must be")]
    [InlineData("""{"type":"click","x":1,"y":1,"button":"back"}""", "button must be")]
    [InlineData("""{"type":"button_down","button":5}""", "button must be a string")]
    [InlineData("""{"type":"scroll","x":1,"y":1}""", "scrolls by nothing")]
    [InlineData("""{"type":"scroll","x":1,"y":1,"delta_y":10001}""", "delta_y must be")]
    [InlineData("""{"type":"type_text","text":""}""", "empty")]
    [InlineData("""{"type":"type_text"}""", "text is required")]
    [InlineData("""{"type":"type_text","text":5}""", "text must be a string")]
    [InlineData("""{"type":"type_text","text":"a\u0007b"}""", "control character")]
    [InlineData("""{"type":"type_text","text":"a\u001bb"}""", "control character")]
    [InlineData("""{"type":"type_text","text":"a\ud800b"}""", "not valid Unicode")]
    [InlineData("""{"type":"type_text","text":"a\udc00b"}""", "not valid Unicode")]
    [InlineData("5", "must be an object")]
    public async Task Invalid_actions_are_refused_without_a_display_call(string action, string fragment) =>
        await AssertRefused(Args(action), fragment);

    [Theory]
    [InlineData("""{"target":"linux","frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""", "frame_width is required")]
    [InlineData("""{"target":"linux","frame_width":0,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""", "frame_width must be")]
    [InlineData("""{"target":"linux","frame_width":3841,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""", "frame_width must be")]
    [InlineData("""{"target":"linux","frame_width":10,"frame_height":2161,"actions":[{"type":"key","key":"Tab"}]}""", "frame_height must be")]
    [InlineData("""{"target":"linux","frame_width":10,"frame_height":10}""", "actions must be an array")]
    [InlineData("""{"target":"linux","frame_width":10,"frame_height":10,"actions":"Tab"}""", "actions must be an array")]
    [InlineData("""{"target":"linux","frame_width":10,"frame_height":10,"actions":[]}""", "must not be empty")]
    [InlineData("""{"target":"mac","frame_width":10,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""", "target must be")]
    [InlineData("""{"target":"linux","source":"browser","frame_width":10,"frame_height":10,"actions":[{"type":"key","key":"Tab"}]}""", "browser source")]
    public async Task Invalid_top_level_arguments_are_refused(string args, string fragment) => await AssertRefused(args, fragment);

    [Fact]
    public async Task Text_longer_than_the_cap_is_refused_and_text_at_the_cap_is_sent()
    {
        var options = new ScreenToolsOptions { MaxTextLength = 5 };
        await AssertRefused(Args("""{"type":"type_text","text":"abcdef"}"""), "too long", options);

        var (json, _) = await Run(Args("""{"type":"type_text","text":"abcde"}"""), options);
        Assert.True(json.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Newline_and_tab_are_allowed_in_typed_text()
    {
        var actions = await Sent("""{"type":"type_text","text":"one\ntwo\tthree"}""");
        Assert.Equal(new TypeText("one\ntwo\tthree"), Assert.Single(actions));
    }

    [Fact]
    public async Task Astral_characters_are_allowed_in_typed_text()
    {
        var actions = await Sent("""{"type":"type_text","text":"smile 😀"}""");
        Assert.Equal(new TypeText("smile \U0001F600"), Assert.Single(actions));
    }

    [Fact]
    public async Task The_action_count_cap_applies_and_the_cap_itself_is_accepted()
    {
        var options = new ScreenToolsOptions { MaxActionsPerCall = 3 };
        string one = """{"type":"key","key":"Tab"}""";
        await AssertRefused(Args(string.Join(',', Enumerable.Repeat(one, 4))), "above the limit of 3", options);

        var (json, _) = await Run(Args(string.Join(',', Enumerable.Repeat(one, 3))), options);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(3, Assert.Single(_display.Inputs).Actions.Count);
    }

    [Fact]
    public async Task The_default_cap_is_twenty()
    {
        string one = """{"type":"key","key":"Tab"}""";
        await AssertRefused(Args(string.Join(',', Enumerable.Repeat(one, 21))), "above the limit of 20");
        var (json, _) = await Run(Args(string.Join(',', Enumerable.Repeat(one, 20))));
        Assert.True(json.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Options_cannot_loosen_the_hard_ceilings_or_go_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenToolsOptions { MaxActionsPerCall = 51 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenToolsOptions { MaxActionsPerCall = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenToolsOptions { MaxImageBytes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenToolsOptions { MaxImageBytes = ScreenToolsOptions.HardMaxImageBytes + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenToolsOptions { MaxTextLength = 2001 }.Validate());
        new ScreenToolsOptions { MaxActionsPerCall = 50 }.Validate();
        new ScreenToolsOptions().Validate();
    }

    [Fact]
    public async Task Coordinates_pass_through_unchanged_when_the_native_size_is_unknown()
    {
        var actions = await Sent("""{"type":"move","x":999,"y":499}""", native: new FakeNative(null));
        Assert.Equal(new PointerMove(999, 499), Assert.Single(actions));
    }

    [Fact]
    public async Task Coordinates_scale_from_the_frame_to_the_native_size()
    {
        var actions = await Sent(
            """
            {"type":"move","x":500,"y":250},
            {"type":"click","x":0,"y":0},
            {"type":"click","x":999,"y":499,"count":2},
            {"type":"scroll","x":100,"y":100,"delta_y":2}
            """,
            native: new FakeNative((2000, 1000)));

        Assert.Equal(
            [
                new PointerMove(1000, 500),
                new PointerClick(0, 0),
                new PointerClick(1998, 998, PointerButtonKind.Left, 2),
                new PointerScroll(200, 200, 0, 2),
            ],
            actions);
    }

    [Theory]
    [InlineData(1, 3, 10, 3)]
    [InlineData(1, 2, 3, 2)]
    [InlineData(1, 3, 4, 1)]
    [InlineData(2, 3, 4, 3)]
    [InlineData(0, 100, 7, 0)]
    [InlineData(99, 100, 7, 6)]
    [InlineData(5, 10, 10, 5)]
    [InlineData(4, 10, 5, 2)]
    public void Scale_rounds_half_away_from_zero_and_stays_inside_the_native_bounds(int value, int frame, int native, int expected) =>
        Assert.Equal(expected, ScreenInputTool.Scale(value, frame, native));

    [Fact]
    public void Scale_never_leaves_the_native_bounds_for_any_in_frame_value()
    {
        foreach ((int frame, int native) in new[] { (1920, 3840), (3840, 1920), (1000, 999), (7, 2160), (2160, 7), (1, 1) })
        {
            for (int v = 0; v < frame; v++)
            {
                int s = ScreenInputTool.Scale(v, frame, native);
                Assert.InRange(s, 0, native - 1);
            }
        }
    }

    [Fact]
    public async Task A_native_size_outside_the_ceiling_is_ignored()
    {
        var actions = await Sent("""{"type":"move","x":10,"y":10}""", native: new FakeNative((100000, 100000)));
        Assert.Equal(new PointerMove(10, 10), Assert.Single(actions));
    }

    [Fact]
    public async Task An_agent_without_the_target_gets_no_target()
    {
        string raw = await Tool().ExecuteAsync(Rig.Json(Args("""{"type":"key","key":"Tab"}""")), Rig.Context(Rig.OtherAgent), TestContext.Current.CancellationToken);

        Assert.Equal("no_target", Rig.Parse(raw).GetProperty("status").GetString());
        Assert.Empty(_display.Inputs);
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
        _display.Fail = new DisplayException(failure, "hello world detail from /var/lib/vm");
        var (json, raw) = await Run(Args("""{"type":"key","key":"Tab"}"""));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("display_failed", json.GetProperty("status").GetString());
        Assert.Equal(name, json.GetProperty("failure").GetString());
        Assert.DoesNotContain("/var/lib", raw);
    }

    [Fact]
    public async Task Typed_text_is_never_echoed_in_a_result()
    {
        var (_, ok) = await Run(Args("""{"type":"type_text","text":"hello world"}"""));
        _display.Fail = new DisplayException(DisplayFailure.Refused, "hello world");
        var (_, failed) = await Run(Args("""{"type":"type_text","text":"hello world"}"""));

        Assert.DoesNotContain("hello world", ok);
        Assert.DoesNotContain("hello world", failed);
    }

    [Fact]
    public async Task Cancellation_flows_through()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool().ExecuteAsync(Rig.Json(Args("""{"type":"key","key":"Tab"}""")), Rig.Context(), cts.Token));
    }

    [Fact]
    public void The_definition_is_well_formed_and_forbids_extra_fields()
    {
        Assert.Equal("screen.input", ScreenInputTool.Definition.Name);
        var schema = Rig.Parse(ScreenInputTool.Definition.ParametersJsonSchema);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("properties").GetProperty("actions").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(ScreenToolsOptions.HardMaxActions, schema.GetProperty("properties").GetProperty("actions").GetProperty("maxItems").GetInt32());
        Assert.NotEmpty(Encoding.UTF8.GetBytes(ScreenInputTool.Definition.Description));
    }
}
