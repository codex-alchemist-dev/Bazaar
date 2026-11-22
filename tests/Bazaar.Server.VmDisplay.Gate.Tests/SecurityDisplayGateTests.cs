using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.Gate.Tests;

public class SecurityDisplayGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Built by concatenation so no secret-looking literal sits in the source.
    private static readonly string _secret = "hunter" + "-two";

    private sealed class Rig
    {
        public FakeKillSwitch Kill { get; } = new();

        public FakeTaint Taint { get; } = new();

        public FakeMatcher Matcher { get; } = new();

        public FakeClassifier? Classifier { get; set; }

        public FakeApproval? Approval { get; set; }

        public FakeTimeProvider Time { get; } = new();

        public DisplayGateOptions Options { get; set; } = new() { Enabled = true };

        public SecurityDisplayGate Gate => new(Options, Kill, Taint, Matcher, Classifier, Approval, Time);

        public int ReaderCalls => Kill.Calls + Taint.Calls + Matcher.Seen.Count;
    }

    private static DisplayTarget Vm(string agent = "agent-a") => new(agent, Guid.NewGuid(), DisplayGuestOs.Linux, "backend-x");

    private static DisplayTarget Browser(string agent = "agent-a") => new(agent, Guid.NewGuid(), DisplayGuestOs.Linux, "backend-x", "session-1");

    private static IReadOnlyList<InputAction> Keys => [new KeyStroke("Tab")];

    private static void AssertDenied(DisplayVerdict v, string reason)
    {
        Assert.False(v.Allowed);
        Assert.Equal(reason, v.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Blank_agent_is_denied_first(string agent)
    {
        var r = new Rig();

        AssertDenied(await r.Gate.CheckCaptureAsync(Vm(agent), Ct), SecurityDisplayGate.ReasonNoAgent);
        AssertDenied(await r.Gate.CheckInputAsync(Vm(agent), Keys, Ct), SecurityDisplayGate.ReasonNoAgent);
        Assert.Equal(0, r.ReaderCalls);
    }

    [Fact]
    public async Task Default_options_make_real_decisions()
    {
        var r = new Rig { Options = new DisplayGateOptions() };

        Assert.True(r.Options.Enabled);
        Assert.True((await r.Gate.CheckCaptureAsync(Vm(), Ct)).Allowed);
        r.Kill.Frozen = true;
        AssertDenied(await r.Gate.CheckCaptureAsync(Vm(), Ct), SecurityDisplayGate.ReasonFrozen);
        r.Kill.Frozen = false;
        r.Taint.State = TaintState.Hard;
        AssertDenied(await r.Gate.CheckInputAsync(Browser(), Keys, Ct), SecurityDisplayGate.ReasonHardTaintAccount);
    }

    [Fact]
    public async Task Switched_off_refuses_everything_and_calls_no_reader()
    {
        var r = new Rig { Options = new DisplayGateOptions { Enabled = false } };

        Assert.False(r.Options.Enabled);
        AssertDenied(await r.Gate.CheckCaptureAsync(Vm(), Ct), SecurityDisplayGate.ReasonNotEnabled);
        AssertDenied(await r.Gate.CheckInputAsync(Vm(), [new TypeText("x")], Ct), SecurityDisplayGate.ReasonNotEnabled);
        Assert.Equal(0, r.ReaderCalls);
    }

    [Fact]
    public async Task Frozen_agent_is_denied_before_matcher_or_taint_is_read()
    {
        var r = new Rig();
        r.Kill.Frozen = true;

        AssertDenied(await r.Gate.CheckCaptureAsync(Vm(), Ct), SecurityDisplayGate.ReasonFrozen);
        AssertDenied(await r.Gate.CheckInputAsync(Vm(), [new TypeText("x")], Ct), SecurityDisplayGate.ReasonFrozen);
        Assert.Equal(0, r.Taint.Calls);
        Assert.Empty(r.Matcher.Seen);
    }

    [Fact]
    public async Task Capture_is_allowed_when_not_frozen_whatever_the_taint()
    {
        var r = new Rig();
        r.Taint.State = TaintState.Hard;

        Assert.True((await r.Gate.CheckCaptureAsync(Browser(), Ct)).Allowed);
        Assert.Equal(0, r.Taint.Calls);
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    public async Task Clean_and_soft_taint_allow_input_in_every_mode(TaintState state)
    {
        foreach (InputUnderHardTaint mode in Enum.GetValues<InputUnderHardTaint>())
        {
            var r = new Rig { Options = new DisplayGateOptions { Enabled = true, InputUnderHardTaint = mode } };
            r.Taint.State = state;

            Assert.True((await r.Gate.CheckInputAsync(Vm(), Keys, Ct)).Allowed);
            Assert.True((await r.Gate.CheckInputAsync(Browser(), Keys, Ct)).Allowed);
        }
    }

    public enum Where
    {
        OwnVm,
        Anonymous,
        Account,
        NoClassifier,
        ClassifierThrows,
        ClassifierHangs,
    }

    private static Rig HardRig(InputUnderHardTaint mode, Where where, out DisplayTarget target)
    {
        var r = new Rig { Options = new DisplayGateOptions { Enabled = true, InputUnderHardTaint = mode, ReadTimeoutSeconds = 1 } };
        r.Taint.State = TaintState.Hard;
        target = where == Where.OwnVm ? Vm() : Browser();
        r.Classifier = where switch
        {
            Where.Anonymous => new FakeClassifier { Class = BrowserSessionClass.Anonymous },
            Where.Account => new FakeClassifier { Class = BrowserSessionClass.Account },
            Where.ClassifierThrows => new FakeClassifier { Mode = Behaviour.Throw, Class = BrowserSessionClass.Anonymous },
            Where.ClassifierHangs => new FakeClassifier { Mode = Behaviour.Never, Class = BrowserSessionClass.Anonymous },
            _ => null,
        };
        return r;
    }

    [Theory]
    [InlineData(Where.Account)]
    [InlineData(Where.NoClassifier)]
    [InlineData(Where.ClassifierThrows)]
    public async Task Account_browsers_are_denied_under_hard_taint_in_every_mode(Where where)
    {
        foreach (InputUnderHardTaint mode in Enum.GetValues<InputUnderHardTaint>())
        {
            Rig r = HardRig(mode, where, out DisplayTarget target);
            r.Approval = new FakeApproval { Approve = true };

            AssertDenied(await r.Gate.CheckInputAsync(target, Keys, Ct), SecurityDisplayGate.ReasonHardTaintAccount);
            Assert.Equal(0, r.Approval.Calls);
        }
    }

    [Fact]
    public async Task A_classifier_that_never_answers_counts_as_account_after_the_timeout()
    {
        Rig r = HardRig(InputUnderHardTaint.AccountBrowsers, Where.ClassifierHangs, out DisplayTarget target);

        ValueTask<DisplayVerdict> pending = r.Gate.CheckInputAsync(target, Keys, Ct);
        await Task.Yield();
        r.Time.Advance(TimeSpan.FromSeconds(1));

        AssertDenied(await pending, SecurityDisplayGate.ReasonHardTaintAccount);
    }

    [Theory]
    [InlineData(Where.OwnVm)]
    [InlineData(Where.Anonymous)]
    public async Task Account_browsers_mode_allows_own_vm_and_anonymous_under_hard_taint(Where where)
    {
        Rig r = HardRig(InputUnderHardTaint.AccountBrowsers, where, out DisplayTarget target);

        Assert.True((await r.Gate.CheckInputAsync(target, Keys, Ct)).Allowed);
    }

    [Theory]
    [InlineData(Where.OwnVm)]
    [InlineData(Where.Anonymous)]
    public async Task All_mode_denies_everything_under_hard_taint(Where where)
    {
        Rig r = HardRig(InputUnderHardTaint.All, where, out DisplayTarget target);

        AssertDenied(await r.Gate.CheckInputAsync(target, Keys, Ct), SecurityDisplayGate.ReasonHardTaintAll);
    }

    [Theory]
    [InlineData(Where.OwnVm)]
    [InlineData(Where.Anonymous)]
    public async Task Approval_mode_asks_the_port(Where where)
    {
        Rig r = HardRig(InputUnderHardTaint.Approval, where, out DisplayTarget target);
        r.Approval = new FakeApproval { Approve = true };
        Assert.True((await r.Gate.CheckInputAsync(target, Keys, Ct)).Allowed);

        r.Approval.Approve = false;
        AssertDenied(await r.Gate.CheckInputAsync(target, Keys, Ct), SecurityDisplayGate.ReasonApprovalRequired);
        Assert.Equal(2, r.Approval.Calls);
    }

    [Fact]
    public async Task Approval_mode_without_a_port_denies()
    {
        Rig r = HardRig(InputUnderHardTaint.Approval, Where.OwnVm, out DisplayTarget target);

        AssertDenied(await r.Gate.CheckInputAsync(target, Keys, Ct), SecurityDisplayGate.ReasonApprovalRequired);
    }

    [Fact]
    public async Task Approval_port_failure_denies()
    {
        Rig r = HardRig(InputUnderHardTaint.Approval, Where.OwnVm, out DisplayTarget target);
        r.Approval = new FakeApproval { Approve = true, Mode = Behaviour.Throw };

        AssertDenied(await r.Gate.CheckInputAsync(target, Keys, Ct), SecurityDisplayGate.ReasonReadFailed);
    }

    [Fact]
    public async Task Typing_a_leased_credential_is_denied_and_the_reason_hides_the_text()
    {
        var r = new Rig();
        r.Matcher.Secret = _secret;
        string typed = "login " + _secret + " now";

        DisplayVerdict v = await r.Gate.CheckInputAsync(Vm(), [new KeyStroke("Tab"), new TypeText(typed)], Ct);

        AssertDenied(v, SecurityDisplayGate.ReasonCredential);
        Assert.DoesNotContain(_secret, v.Reason, StringComparison.Ordinal);
        Assert.Equal(0, r.Taint.Calls);
    }

    [Fact]
    public async Task A_secret_split_across_two_type_actions_is_denied()
    {
        var r = new Rig();
        r.Matcher.Secret = _secret;

        DisplayVerdict v = await r.Gate.CheckInputAsync(Vm(), [new TypeText("hunter"), new KeyStroke("Tab"), new TypeText("-two")], Ct);

        AssertDenied(v, SecurityDisplayGate.ReasonCredential);
        Assert.Contains(_secret, r.Matcher.Seen);
    }

    [Fact]
    public async Task A_secret_pressed_one_key_at_a_time_is_denied()
    {
        var r = new Rig();
        r.Matcher.Secret = "ab" + "C1 d";

        DisplayVerdict v = await r.Gate.CheckInputAsync(
            Vm(),
            [new KeyStroke("KeyA"), new KeyStroke("KeyB"), new KeyStroke("KeyC", KeyModifiers.Shift), new KeyStroke("Digit1"), new KeyStroke("Space"), new KeyStroke("KeyD")],
            Ct);

        AssertDenied(v, SecurityDisplayGate.ReasonCredential);
    }

    [Fact]
    public async Task A_secret_split_between_typed_text_and_key_strokes_is_denied()
    {
        var r = new Rig();
        r.Matcher.Secret = _secret;

        DisplayVerdict v = await r.Gate.CheckInputAsync(Vm(), [new TypeText("hunter-tw"), new KeyStroke("KeyO")], Ct);

        AssertDenied(v, SecurityDisplayGate.ReasonCredential);
    }

    [Fact]
    public async Task Shortcut_strokes_and_non_character_keys_type_nothing()
    {
        Assert.Null(SecurityDisplayGate.KeyCharacter(new KeyStroke("KeyC", KeyModifiers.Control)));
        Assert.Null(SecurityDisplayGate.KeyCharacter(new KeyStroke("KeyV", KeyModifiers.Shift | KeyModifiers.Meta)));
        Assert.Null(SecurityDisplayGate.KeyCharacter(new KeyStroke("Enter")));
        Assert.Null(SecurityDisplayGate.KeyCharacter(new KeyStroke("Digit1", KeyModifiers.Shift)));
        Assert.Null(SecurityDisplayGate.KeyCharacter(new KeyStroke("F1")));
        Assert.Equal('z', SecurityDisplayGate.KeyCharacter(new KeyStroke("KeyZ")));
        Assert.Equal('Z', SecurityDisplayGate.KeyCharacter(new KeyStroke("KeyZ", KeyModifiers.Shift)));

        var r = new Rig();
        r.Matcher.Secret = _secret;
        Assert.True((await r.Gate.CheckInputAsync(Vm(), [new KeyStroke("KeyC", KeyModifiers.Control), new KeyStroke("Enter")], Ct)).Allowed);
        Assert.Empty(r.Matcher.Seen);
    }

    [Fact]
    public async Task Harmless_text_is_allowed_and_empty_text_is_not_sent_to_the_matcher()
    {
        var r = new Rig();
        r.Matcher.Secret = _secret;

        Assert.True((await r.Gate.CheckInputAsync(Vm(), [new TypeText("hello"), new TypeText(""), new TypeText("world")], Ct)).Allowed);
        Assert.Equal(["hello", "world", "helloworld"], r.Matcher.Seen);
    }

    [Fact]
    public async Task Credential_check_runs_before_the_taint_read()
    {
        var r = new Rig();
        r.Matcher.Secret = _secret;
        r.Taint.Mode = Behaviour.Throw;

        AssertDenied(await r.Gate.CheckInputAsync(Vm(), [new TypeText(_secret)], Ct), SecurityDisplayGate.ReasonCredential);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("taint")]
    [InlineData("matcher")]
    public async Task A_throwing_reader_denies_with_a_fixed_reason(string which)
    {
        var r = new Rig();
        Arrange(r, which, Behaviour.Throw);

        DisplayVerdict v = await r.Gate.CheckInputAsync(Vm(), [new TypeText("hello")], Ct);

        AssertDenied(v, SecurityDisplayGate.ReasonReadFailed);
        Assert.DoesNotContain("private detail", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_kill_switch_denies_capture()
    {
        var r = new Rig();
        r.Kill.Mode = Behaviour.Throw;

        AssertDenied(await r.Gate.CheckCaptureAsync(Vm(), Ct), SecurityDisplayGate.ReasonReadFailed);
    }

    [Fact]
    public async Task A_reader_that_cancels_itself_without_the_caller_cancelling_denies()
    {
        var r = new Rig();
        r.Taint.Mode = Behaviour.Cancelled;

        AssertDenied(await r.Gate.CheckInputAsync(Vm(), Keys, Ct), SecurityDisplayGate.ReasonReadFailed);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("taint")]
    [InlineData("matcher")]
    public async Task A_reader_that_never_completes_is_denied_at_the_timeout(string which)
    {
        var r = new Rig();
        Arrange(r, which, Behaviour.Never);

        ValueTask<DisplayVerdict> pending = r.Gate.CheckInputAsync(Vm(), [new TypeText("hello")], Ct);
        await Task.Yield();
        r.Time.Advance(TimeSpan.FromSeconds(r.Options.ReadTimeoutSeconds));

        AssertDenied(await pending, SecurityDisplayGate.ReasonReadTimedOut);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_even_when_a_reader_hangs()
    {
        var r = new Rig();
        r.Kill.Mode = Behaviour.Never;
        using var cts = new CancellationTokenSource();

        ValueTask<DisplayVerdict> pending = r.Gate.CheckCaptureAsync(Vm(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task An_already_cancelled_token_propagates()
    {
        var r = new Rig();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await r.Gate.CheckInputAsync(Vm(), Keys, new CancellationToken(true)));
        Assert.Equal(0, r.ReaderCalls);
    }

    [Fact]
    public async Task A_missing_reader_denies_everything()
    {
        var options = new DisplayGateOptions { Enabled = true };
        var kill = new FakeKillSwitch();
        var taint = new FakeTaint();
        var matcher = new FakeMatcher();
        SecurityDisplayGate[] gates =
        [
            new(options, null, taint, matcher),
            new(options, kill, null, matcher),
            new(options, kill, taint, null),
        ];

        foreach (SecurityDisplayGate gate in gates)
        {
            AssertDenied(await gate.CheckCaptureAsync(Vm(), Ct), SecurityDisplayGate.ReasonReadsUnavailable);
            AssertDenied(await gate.CheckInputAsync(Vm(), Keys, Ct), SecurityDisplayGate.ReasonReadsUnavailable);
        }

        Assert.Equal(0, kill.Calls + taint.Calls + matcher.Seen.Count);
    }

    [Fact]
    public void Every_reason_is_a_fixed_string_without_typed_text()
    {
        string[] reasons =
        [
            SecurityDisplayGate.ReasonNoAgent, SecurityDisplayGate.ReasonNotEnabled, SecurityDisplayGate.ReasonReadsUnavailable,
            SecurityDisplayGate.ReasonFrozen, SecurityDisplayGate.ReasonReadFailed, SecurityDisplayGate.ReasonReadTimedOut,
            SecurityDisplayGate.ReasonCredential, SecurityDisplayGate.ReasonHardTaintAccount, SecurityDisplayGate.ReasonHardTaintAll,
            SecurityDisplayGate.ReasonApprovalRequired,
        ];

        Assert.All(reasons, x => Assert.DoesNotContain(_secret, x, StringComparison.Ordinal));
        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }

    private static void Arrange(Rig r, string which, Behaviour mode)
    {
        switch (which)
        {
            case "kill":
                r.Kill.Mode = mode;
                break;
            case "taint":
                r.Taint.Mode = mode;
                break;
            default:
                r.Matcher.Mode = mode;
                break;
        }
    }
}
