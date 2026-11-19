using System.Globalization;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Tests;

public class InputTests
{
    private static InputBatch Batch(DisplayTarget t, params InputAction[] actions) => new(t, actions);

    private static async Task<DisplayException> Fails(Task t) => await Assert.ThrowsAsync<DisplayException>(() => t);

    private static Rig NewRig() => new();

    // Validation

    public static TheoryData<InputAction> InvalidActions() => new()
    {
        new KeyStroke("KeyAA"),
        new KeyStroke("a"),
        new KeyStroke("enter"),
        new KeyStroke("Meta"),
        new KeyStroke("F13"),
        new KeyStroke("F0"),
        new KeyStroke(""),
        new KeyStroke("KeyA", (KeyModifiers)64),
        new PointerMove(-1, 0),
        new PointerMove(0, -1),
        new PointerMove(DisplayLimits.CeilingWidth, 0),
        new PointerMove(0, DisplayLimits.CeilingHeight),
        new PointerClick(-5, 5),
        new PointerClick(5, 5, PointerButtonKind.Left, 0),
        new PointerClick(5, 5, PointerButtonKind.Left, 4),
        new PointerClick(5, 5, (PointerButtonKind)9),
        new PointerButton((PointerButtonKind)9, true),
        new PointerScroll(-1, 0, 0, 0),
        new PointerScroll(0, 0, 0, 100_000),
        new PointerScroll(0, 0, int.MinValue, 0),
        new TypeText(""),
        new TypeText("bell\a"),
        new TypeText("nul\0"),
        new TypeText("esc\u001b[2J"),
        new TypeText("cr\rlf"),
        new TypeText("del\u007f"),
        new TypeText("lone\ud800surrogate"),
        new TypeText("\udc00"),
        new TypeText(new string('a', 2001)),
    };

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public async Task Invalid_actions_are_rejected_before_any_source_is_touched(InputAction action)
    {
        Rig r = NewRig();
        DisplayException e = await Fails(r.Build().SendInputAsync(Batch(Rig.Linux(), action), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(r.Guest.Execs);
        Assert.Empty(r.Hypervisor.Inputs);
        Assert.Empty(r.Browser.Inputs);
        Assert.Contains(r.Audit.Rows, x => x.StartsWith("refused:input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Newline_and_tab_are_allowed_in_typed_text()
    {
        Rig r = NewRig();
        await r.Build().SendInputAsync(Batch(Rig.Windows(), new TypeText("line one\n\tline two \U0001F600")), TestContext.Current.CancellationToken);
        Assert.Single(r.Hypervisor.Inputs);
    }

    [Fact]
    public async Task Text_exactly_at_the_limit_is_accepted()
    {
        Rig r = NewRig();
        await r.Build().SendInputAsync(Batch(Rig.Windows(), new TypeText(new string('a', 2000))), TestContext.Current.CancellationToken);
        Assert.Single(r.Hypervisor.Inputs);
    }

    [Fact]
    public async Task Every_allowlisted_key_validates()
    {
        Rig r = NewRig();
        r.Options.MaxActionsPerBatch = 1000;
        InputAction[] all = [.. DisplayKeys.All.Select(k => (InputAction)new KeyStroke(k, KeyModifiers.Control))];
        await r.Build().SendInputAsync(Batch(Rig.Windows(), all), TestContext.Current.CancellationToken);
        Assert.Equal(DisplayKeys.All.Count, r.Hypervisor.Inputs[0].Count);
    }

    [Fact]
    public void The_key_allowlist_has_the_documented_shape()
    {
        Assert.Equal(26 + 10 + 12 + 14, DisplayKeys.All.Count);
        Assert.True(DisplayKeys.IsAllowed("KeyZ"));
        Assert.True(DisplayKeys.IsAllowed("Digit0"));
        Assert.True(DisplayKeys.IsAllowed("F12"));
        Assert.True(DisplayKeys.IsAllowed("PageDown"));
        Assert.False(DisplayKeys.IsAllowed(null));
    }

    [Fact]
    public async Task Over_long_and_empty_batches_are_rejected()
    {
        Rig r = NewRig();
        GuestDisplayService s = r.Build();
        InputAction[] many = [.. Enumerable.Repeat<InputAction>(new PointerMove(1, 1), 201)];
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(s.SendInputAsync(Batch(Rig.Linux(), many), TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(s.SendInputAsync(Batch(Rig.Linux()), TestContext.Current.CancellationToken))).Failure);
        InputAction[] exactly = [.. Enumerable.Repeat<InputAction>(new PointerMove(1, 1), 200)];
        await s.SendInputAsync(Batch(Rig.Windows(), exactly), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unknown_action_type_is_rejected()
    {
        Rig r = NewRig();
        DisplayException e = await Fails(r.Build().SendInputAsync(Batch(Rig.Linux(), new CustomAction()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
    }

    private sealed record CustomAction : InputAction;

    // Dispatch, gate, limits

    [Fact]
    public async Task Input_goes_to_the_browser_for_a_browser_session()
    {
        Rig r = NewRig();
        InputAction[] actions = [new PointerClick(10, 20), new KeyStroke("Enter")];
        await r.Build().SendInputAsync(Batch(Rig.Linux("s1"), actions), TestContext.Current.CancellationToken);
        Assert.Equal("s1", r.Browser.LastSession);
        Assert.Equal(actions, r.Browser.Inputs[0]);
        Assert.Contains("input:Browser:2", r.Audit.Rows);
    }

    [Fact]
    public async Task Input_to_windows_goes_to_the_hypervisor()
    {
        Rig r = NewRig();
        await r.Build().SendInputAsync(Batch(Rig.Windows(), new TypeText("hi")), TestContext.Current.CancellationToken);
        Assert.Single(r.Hypervisor.Inputs);
        Assert.Contains("input:Hypervisor:1", r.Audit.Rows);
    }

    [Fact]
    public async Task Input_to_linux_runs_fixed_tools_in_the_guest()
    {
        Rig r = NewRig();
        await r.Build().SendInputAsync(Batch(Rig.Linux(), new TypeText("hello")), TestContext.Current.CancellationToken);
        Assert.Equal(["wtype", "-k", "Shift_L", "--", "hello"], Assert.Single(r.Guest.Execs));
        Assert.Contains("input:Guest:1", r.Audit.Rows);
    }

    [Fact]
    public async Task Input_never_falls_back_after_a_source_failure()
    {
        Rig r = NewRig();
        r.Guest.ExitFor = _ => 1;
        DisplayException e = await Fails(r.Build().SendInputAsync(Batch(Rig.Linux(), new KeyStroke("Tab")), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Empty(r.Hypervisor.Inputs);
        Assert.DoesNotContain(r.Audit.Rows, x => x.StartsWith("input:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Guest_input_stops_at_the_first_failing_command()
    {
        Rig r = NewRig();
        r.Guest.ExitFor = argv => argv[0] == "wtype" ? 1 : 0;
        InputAction[] actions = [new KeyStroke("Tab"), new TypeText("later")];
        DisplayException e = await Fails(r.Build().SendInputAsync(Batch(Rig.Linux(), actions), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Single(r.Guest.Execs);
    }

    [Fact]
    public async Task A_named_unavailable_source_reports_no_source()
    {
        Rig r = NewRig();
        var batch = new InputBatch(Rig.Windows(), [new KeyStroke("Tab")], DisplaySourceKind.Guest);
        Assert.Equal(DisplayFailure.NoSourceAvailable, (await Fails(r.Build().SendInputAsync(batch, TestContext.Current.CancellationToken))).Failure);
    }

    [Fact]
    public async Task The_gate_sees_the_actions_and_can_refuse_typed_text()
    {
        Rig r = NewRig();
        var gate = new TextRefusingGate();
        var svc = new GuestDisplayService(r.Options, gate, r.Audit, r.Time, [r.Hypervisor], [], []);
        DisplayException e = await Fails(svc.SendInputAsync(Batch(Rig.Windows(), new TypeText("typed words")), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.Refused, e.Failure);
        Assert.Empty(r.Hypervisor.Inputs);
        Assert.DoesNotContain("typed words", e.Message, StringComparison.Ordinal);
        await svc.SendInputAsync(Batch(Rig.Windows(), new KeyStroke("Tab")), TestContext.Current.CancellationToken);
    }

    private sealed class TextRefusingGate : IDisplayGate
    {
        public DisplayVerdict CheckCapture(DisplayTarget target) => DisplayVerdict.Allow;

        public DisplayVerdict CheckInput(DisplayTarget target, IReadOnlyList<InputAction> actions) =>
            actions.OfType<TypeText>().Any() ? DisplayVerdict.Deny("typing is not allowed here") : DisplayVerdict.Allow;
    }

    [Fact]
    public async Task Disabled_module_refuses_input()
    {
        Rig r = NewRig();
        r.Options.Enabled = false;
        Assert.Equal(DisplayFailure.Disabled, (await Fails(r.Build().SendInputAsync(Batch(Rig.Windows(), new KeyStroke("Tab")), TestContext.Current.CancellationToken))).Failure);
    }

    [Fact]
    public async Task Input_rate_limit_is_per_agent_and_separate_from_capture()
    {
        Rig r = NewRig();
        r.Options.InputBatchesPerMinute = 1;
        GuestDisplayService s = r.Build();
        await s.SendInputAsync(Batch(Rig.Windows(), new KeyStroke("Tab")), TestContext.Current.CancellationToken);
        Assert.Equal(DisplayFailure.RateLimited, (await Fails(s.SendInputAsync(Batch(Rig.Windows(), new KeyStroke("Tab")), TestContext.Current.CancellationToken))).Failure);
        await s.SendInputAsync(Batch(Rig.Windows(agent: "other"), new KeyStroke("Tab")), TestContext.Current.CancellationToken);
        r.Time.Advance(TimeSpan.FromMinutes(2));
        await s.SendInputAsync(Batch(Rig.Windows(), new KeyStroke("Tab")), TestContext.Current.CancellationToken);
    }

    // LinuxInputArgv

    [Fact]
    public void Type_text_is_one_argv_element_after_a_double_dash()
    {
        IReadOnlyList<IReadOnlyList<string>> argv = LinuxInputArgv.Build([new TypeText("hello world")]);
        Assert.Equal(["wtype", "-k", "Shift_L", "--", "hello world"], Assert.Single(argv));
    }

    [Theory]
    [InlineData("-k Return")]
    [InlineData("--help")]
    [InlineData("-M ctrl")]
    [InlineData("-")]
    [InlineData("; rm -rf / #")]
    [InlineData("$(reboot) `id` && x")]
    public void Text_beginning_with_a_dash_or_holding_shell_syntax_stays_a_single_literal_argument(string text)
    {
        IReadOnlyList<string> cmd = Assert.Single(LinuxInputArgv.Build([new TypeText(text)]));
        Assert.Equal(5, cmd.Count);
        Assert.Equal("--", cmd[3]);
        Assert.Equal(text, cmd[4]);
    }

    [Fact]
    public void Key_strokes_map_to_xkb_names_with_modifiers_around_the_key()
    {
        Assert.Equal(["wtype", "-k", "Shift_L", "-k", "Return"], Assert.Single(LinuxInputArgv.Build([new KeyStroke("Enter")])));
        Assert.Equal(
            ["wtype", "-M", "shift", "-M", "ctrl", "-k", "a", "-m", "ctrl", "-m", "shift"],
            Assert.Single(LinuxInputArgv.Build([new KeyStroke("KeyA", KeyModifiers.Control | KeyModifiers.Shift)])));
        Assert.Equal(["wtype", "-M", "alt", "-M", "logo", "-k", "F5", "-m", "logo", "-m", "alt"], Assert.Single(LinuxInputArgv.Build([new KeyStroke("F5", KeyModifiers.Alt | KeyModifiers.Meta)])));
        Assert.Equal(["wtype", "-k", "Shift_L", "-k", "7"], Assert.Single(LinuxInputArgv.Build([new KeyStroke("Digit7")])));
    }

    [Theory]
    [InlineData("Backspace", "BackSpace")]
    [InlineData("Space", "space")]
    [InlineData("PageUp", "Prior")]
    [InlineData("PageDown", "Next")]
    [InlineData("ArrowLeft", "Left")]
    [InlineData("Escape", "Escape")]
    [InlineData("KeyQ", "q")]
    [InlineData("F12", "F12")]
    public void Xkb_names(string key, string expected) => Assert.Equal(expected, LinuxInputArgv.XkbName(key));

    [Fact]
    public void Every_allowlisted_key_has_a_distinct_xkb_name()
    {
        string[] names = [.. DisplayKeys.All.Select(k => LinuxInputArgv.XkbName(k) ?? throw new Xunit.Sdk.XunitException($"no mapping for {k}"))];
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    public static TheoryData<InputAction> PointerActions() => new()
    {
        new PointerMove(9, 8),
        new PointerClick(120, 45, PointerButtonKind.Right, 2),
        new PointerScroll(1, 2, 3, -4),
        new PointerButton(PointerButtonKind.Left, true),
    };

    [Theory]
    [MemberData(nameof(PointerActions))]
    public void Pointer_actions_are_refused_by_the_guest_argv_builder(InputAction action)
    {
        DisplayException e = Assert.Throws<DisplayException>(() => LinuxInputArgv.Build([action]));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Equal("Pointer input is not available on the guest source yet; use the browser or hypervisor source.", e.Message);
    }

    [Theory]
    [MemberData(nameof(PointerActions))]
    public async Task A_named_guest_source_with_pointer_input_is_invalid_and_runs_nothing(InputAction action)
    {
        Rig r = NewRig();
        var batch = new InputBatch(Rig.Linux(), [new KeyStroke("Tab"), action], DisplaySourceKind.Guest);
        DisplayException e = await Fails(r.Build().SendInputAsync(batch, TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Contains("Pointer input is not available on the guest source", e.Message);
        Assert.Empty(r.Guest.Execs);
        Assert.Empty(r.Hypervisor.Inputs);
    }

    [Theory]
    [MemberData(nameof(PointerActions))]
    public async Task An_unnamed_pointer_batch_on_linux_goes_to_the_hypervisor(InputAction action)
    {
        Rig r = NewRig();
        InputAction[] actions = [new KeyStroke("Tab"), action];
        await r.Build().SendInputAsync(Batch(Rig.Linux(), actions), TestContext.Current.CancellationToken);
        Assert.Equal(actions, Assert.Single(r.Hypervisor.Inputs));
        Assert.Empty(r.Guest.Execs);
        Assert.Contains("input:Hypervisor:2", r.Audit.Rows);
    }

    [Fact]
    public async Task An_unnamed_pointer_batch_on_linux_without_a_hypervisor_has_no_source()
    {
        Rig r = NewRig();
        r.UseHypervisor = false;
        DisplayException e = await Fails(r.Build().SendInputAsync(Batch(Rig.Linux(), new PointerClick(5, 5)), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.NoSourceAvailable, e.Failure);
        Assert.Contains("pointer", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task Keyboard_only_batches_still_use_the_guest_on_linux()
    {
        Rig r = NewRig();
        await r.Build().SendInputAsync(Batch(Rig.Linux(), new KeyStroke("Tab"), new TypeText("x")), TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Guest.Execs.Count);
        Assert.Empty(r.Hypervisor.Inputs);
    }

    [Fact]
    public void A_mixed_batch_is_refused_before_any_command_is_produced()
    {
        Assert.Throws<DisplayException>(() => LinuxInputArgv.Build([new TypeText("a"), new KeyStroke("Tab"), new PointerMove(1, 1)]));
    }

    [Fact]
    public void Key_strokes_without_modifiers_get_the_warm_up_tap_and_with_modifiers_do_not()
    {
        Assert.Equal(["wtype", "-k", "Shift_L", "-k", "Tab"], Assert.Single(LinuxInputArgv.Build([new KeyStroke("Tab")])));
        Assert.Equal(["wtype", "-M", "shift", "-k", "Tab", "-m", "shift"], Assert.Single(LinuxInputArgv.Build([new KeyStroke("Tab", KeyModifiers.Shift)])));
    }
}
