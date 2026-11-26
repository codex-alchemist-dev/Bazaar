using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class UnattendedRecoveryTests
{
    private static RunbookDefinition Fix(string id) => OneClickFixes.All.Single(f => f.Id == id).Definition;

    private static RunbookStartRequest Start(RunbookDefinition d, bool tainted = false, KodoState state = KodoState.Healthy, string runId = "run1") =>
        new(runId, d.Id, d.Version, null, "owner", new HashSet<string>(StringComparer.Ordinal) { d.RequiredControl },
            new Dictionary<string, string>(StringComparer.Ordinal), tainted, state);

    private static RunbookOptions Allow(params string[] names) => new() { UnattendedRecoveries = names };

    [Fact]
    public void DefaultIsEmpty()
    {
        Assert.Empty(new RunbookOptions().UnattendedRecoveries);
    }

    [Fact]
    public void OnlyTheTwoFixesCarryTheTag()
    {
        Assert.Equal(
            ["fix.host_restart", "fix.slot_switch"],
            OneClickFixes.All.Where(f => f.Recovery is not null).Select(f => f.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(UnattendedRecovery.HostRestart, OneClickFixes.All.Single(f => f.Id == "fix.host_restart").Recovery);
        Assert.Equal(UnattendedRecovery.SlotSwitch, OneClickFixes.All.Single(f => f.Id == "fix.slot_switch").Recovery);
    }

    [Theory]
    [InlineData("isolate")]
    [InlineData("rollback")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("HOST_RESTART")]
    [InlineData("Host_Restart")]
    [InlineData("slot_Switch")]
    [InlineData(" host_restart")]
    [InlineData("host_restart ")]
    [InlineData("host_restart\n")]
    [InlineData("host-restart")]
    [InlineData("fix.host_restart")]
    [InlineData("*")]
    public void AnyOtherValueIsRefusedAtValidationAndByTheExecutor(string value)
    {
        RunbookOptions options = Allow("host_restart", value);

        Assert.Throws<ArgumentException>(options.Validate);
        Harness h = new(Fix("fix.host_restart"), options);
        Assert.Throws<ArgumentException>(() => h.Executor());
    }

    [Fact]
    public void NullEntryIsRefused()
    {
        RunbookOptions options = new() { UnattendedRecoveries = [null!] };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void DuplicatesCollapseAndStayWithinTheTwo()
    {
        RunbookOptions options = Allow("host_restart", "host_restart", "slot_switch");

        options.Validate();
        Assert.Equal(2, UnattendedRecoveries.Parse(options.UnattendedRecoveries).Count);
    }

    [Fact]
    public async Task DefaultEmptyMeansHostRestartAndSlotSwitchWaitForApproval()
    {
        foreach (string id in new[] { "fix.host_restart", "fix.slot_switch" })
        {
            RunbookDefinition d = Fix(id);
            Harness h = new(d);

            RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

            Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
            Assert.DoesNotContain(h.Runner.Calls, c => c.StepKindIsAction());
        }
    }

    [Theory]
    [InlineData("fix.host_restart", "host_restart", "sentinel.restart_host")]
    [InlineData("fix.slot_switch", "slot_switch", "slot.switch_other")]
    public async Task EachActionRunsUnattendedOnlyWhenListed(string id, string name, string operation)
    {
        RunbookDefinition d = Fix(id);
        Harness h = new(d, Allow(name));

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Succeeded, state.Phase);
        Assert.Empty(h.Gate.Requests);
        Assert.Contains(h.Runner.Calls, c => c.Operation == operation);
        Assert.Contains("run_started", h.Journal.Entries.Select(e => e.Kind));
        Assert.Contains(h.Journal.Entries, e => e.Detail == "auto");
    }

    [Theory]
    [InlineData("fix.host_restart", "slot_switch")]
    [InlineData("fix.slot_switch", "host_restart")]
    public async Task ListingOneActionDoesNotEnableTheOther(string id, string name)
    {
        RunbookDefinition d = Fix(id);
        Harness h = new(d, Allow(name));

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Fact]
    public async Task EveryOtherFixStaysGatedWhateverTheSettingSays()
    {
        foreach (OneClickFix fix in OneClickFixes.All.Where(f => f.Recovery is null && f.Definition.ApprovalTier != RunbookApprovalTier.None))
        {
            Harness h = new(fix.Definition, Allow("host_restart", "slot_switch"));

            RunbookRunState state = (await h.Executor().StartAsync(Start(fix.Definition), CancellationToken.None)).State!;

            Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
        }
    }

    [Fact]
    public async Task IsolateFixNeverRunsUnattendedEvenWhenForcedIntoAutoRunbookIdsWithoutTheSetting()
    {
        RunbookDefinition d = Fix("fix.module_disable");
        Harness h = new(d, new RunbookOptions { AutoRunbookIds = new HashSet<string>(StringComparer.Ordinal) { d.Id } });

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Fact]
    public async Task ForcingRestartAndSlotSwitchIntoAutoRunbookIdsDoesNotMakeThemUnattended()
    {
        foreach (string id in new[] { "fix.host_restart", "fix.slot_switch" })
        {
            RunbookDefinition d = Fix(id);
            Harness h = new(d, new RunbookOptions { AutoRunbookIds = new HashSet<string>(StringComparer.Ordinal) { id } });

            RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

            Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
        }
    }

    [Fact]
    public async Task CustomRunbookWithAShippedIdIsNotEnabledBySetting()
    {
        RunbookDefinition shipped = Fix("fix.host_restart");
        RunbookDefinition custom = shipped with { Title = "Custom restart" };
        Harness h = new(custom, Allow("host_restart"));

        RunbookRunState state = (await h.Executor().StartAsync(Start(custom), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Fact]
    public async Task OtherCustomRunbookIsNotEnabledBySetting()
    {
        RunbookDefinition d = ResponderTestKit.Demo();
        Harness h = new(d, Allow("host_restart", "slot_switch"));

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(state: KodoState.Healthy, definition: d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Fact]
    public async Task TaintedInputStaysGated()
    {
        RunbookDefinition d = Fix("fix.host_restart");
        Harness h = new(d, Allow("host_restart"));

        RunbookRunState state = (await h.Executor().StartAsync(Start(d, tainted: true), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Theory]
    [InlineData(KodoState.Degraded)]
    [InlineData(KodoState.Failing)]
    public async Task NotHealthyStartStaysGated(KodoState kodoState)
    {
        RunbookDefinition d = Fix("fix.slot_switch");
        Harness h = new(d, Allow("slot_switch"));

        RunbookRunState state = (await h.Executor().StartAsync(Start(d, state: kodoState), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
    }

    [Theory]
    [InlineData(KodoState.Frozen, "incident_frozen")]
    [InlineData(KodoState.Recovering, "recovery_running")]
    public async Task FrozenAndRecoveringAreRefused(KodoState kodoState, string refusal)
    {
        RunbookDefinition d = Fix("fix.host_restart");
        Harness h = new(d, Allow("host_restart"));

        RunbookStartResult result = await h.Executor().StartAsync(Start(d, state: kodoState), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal(refusal, result.Refusal);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task UnsignedRunbookIsRefusedEvenWhenListed()
    {
        RunbookDefinition d = Fix("fix.host_restart");
        Harness h = new(d, Allow("host_restart"));
        RunbookStartRequest request = Start(d) with { RunbookId = "fix.not_in_catalogue" };

        RunbookStartResult result = await h.Executor().StartAsync(request, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("unknown_runbook", result.Refusal);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public void BadSignatureNeverEntersTheCatalogue()
    {
        RunbookDefinition d = Fix("fix.host_restart");
        RunbookCatalogue catalogue = new(new RsaPkcs1Sha256Verifier(), ResponderTestKit.Key.ExportSubjectPublicKeyInfo());
        RunbookCatalogue catalogue = new(new RsaPkcs1Sha256Verifier(), ResponderTestKit.PublicKey);

        Assert.NotNull(catalogue.TryAdd(d, new byte[256]));
        Assert.Equal(0, catalogue.Count);
    }

    [Fact]
    public async Task WrongExpectedDigestIsRefused()
    {
        RunbookDefinition d = Fix("fix.slot_switch");
        Harness h = new(d, Allow("slot_switch"));

        RunbookStartResult result = await h.Executor().StartAsync(Start(d) with { ExpectedDigest = "00" }, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("tampered_runbook", result.Refusal);
    }

    [Theory]
    [InlineData("fix.host_restart", "host_restart")]
    [InlineData("fix.slot_switch", "slot_switch")]
    public async Task FailedVerifyStopsWithNeedsHumanAndRunsNoRollback(string id, string name)
    {
        RunbookDefinition d = Fix(id);
        Harness h = new(d, Allow(name));
        h.Runner.Script("verify", RunbookStepOutcome.Failed, RunbookStepOutcome.Failed, RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("unattended_recovery_failed", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "undo");
        Assert.Empty(h.Gate.Requests);
    }

    [Fact]
    public async Task FailedActionStopsWithNeedsHumanAndRunsNoRollback()
    {
        RunbookDefinition d = Fix("fix.slot_switch");
        Harness h = new(d, Allow("slot_switch"));
        h.Runner.Script("fix", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "undo");
    }

    [Fact]
    public async Task RetryCapStillBoundsTheVerify()
    {
        RunbookDefinition d = Fix("fix.host_restart");
        Harness h = new(d, new RunbookOptions { UnattendedRecoveries = ["host_restart"], MaxAttemptsCap = 1 });
        h.Runner.Script("verify", RunbookStepOutcome.Failed, RunbookStepOutcome.Succeeded);

        RunbookRunState state = (await h.Executor().StartAsync(Start(d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal(1, h.Runner.Calls.Count(c => c.StepId == "verify"));
    }
}

internal static class UnattendedRecoveryTestExtensions
{
    public static bool StepKindIsAction(this RunbookStepRequest request) => request.Kind == RunbookStepKind.Action;
}
