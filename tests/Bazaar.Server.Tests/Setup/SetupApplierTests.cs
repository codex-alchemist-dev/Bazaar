using System.Text.Json.Nodes;
using Bazaar.Admin.Composition;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The apply: hash check, validation, the instance lock, ordered steps with rollback, and the audit rows. In memory only.</summary>
public sealed class SetupApplierTests
{
    private const string _operator = "operator-test";

    private static SetupPlan ValidPlan() => SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

    private static SetupApplyRequest Request(SetupPlan plan, string? hash = null, SetupStateSnapshot? state = null) =>
        new(plan, hash ?? SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Cli, _operator, "starter", "1.0.0", state);

    private static JsonObject PayloadOf(AuditEntry entry) => (JsonObject)JsonNode.Parse(entry.Payload)!;

    private static SetupApplier Applier(Harness harness, params ISetupStep[] steps) =>
        new(harness.Lock, steps, harness.Audit, new SettingAuditSinkSetupAudit(harness.SettingAudit), harness.Time, RegistrySetupSettingCatalog.Known);

    private static RecordingStep Step(string id, int order, List<string> calls, StepBehaviour behaviour = StepBehaviour.Succeed) =>
        new(id, order, calls, behaviour);

    [Fact]
    public async Task Hash_mismatch_refuses_with_invalid_input_and_runs_nothing_and_writes_nothing()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();

        var result = await Applier(harness, Step("database.schema", 10, calls)).ApplyAsync(Request(plan, new string('0', 64)), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Empty(calls);
        Assert.Empty(harness.Audit.Entries);
        Assert.Empty(harness.SettingAudit.Entries);
        Assert.Equal(0, harness.Lock.Acquisitions);
    }

    [Fact]
    public async Task Invalid_plan_is_refused_with_exit_1_before_the_lock_is_taken()
    {
        var harness = new Harness();
        var plan = new SetupPlan();

        var result = await Applier(harness).ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task Held_plan_does_not_apply_and_exits_2()
    {
        var harness = new Harness();
        var calls = new List<string>();
        // A missing backup target is a hold (doc 45 section 1.7), and the loader still returns the plan with that hold.
        var plan = SetupPlanLoader.Load(SetupFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"), RegistrySetupSettingCatalog.Known).Plan!;

        var result = await Applier(harness, Step("database.schema", 10, calls)).ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Empty(calls);
        Assert.Empty(result.AppliedStepIds);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task Lock_already_held_returns_exit_2_with_no_change()
    {
        var harness = new Harness { Lock = { Held = true } };
        var calls = new List<string>();

        var result = await Applier(harness, Step("database.schema", 10, calls)).ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Contains(result.Issues, issue => issue.Path == "instance" && issue.Severity == SetupIssueSeverity.Hold);
        Assert.Empty(calls);
        Assert.Empty(harness.Audit.Entries);
        Assert.Empty(harness.SettingAudit.Entries);
    }

    [Theory]
    [InlineData(StepBehaviour.ReportFailure)]
    [InlineData(StepBehaviour.Throw)]
    public async Task Failure_on_the_second_of_three_steps_rolls_back_the_first_and_exits_3(StepBehaviour behaviour)
    {
        var harness = new Harness();
        var calls = new List<string>();
        var applier = Applier(
            harness,
            Step("database.schema", 10, calls),
            Step("custody.kek", 20, calls, behaviour),
            Step("owner.identity", 30, calls));

        var result = await applier.ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.ApplyFailedRolledBack, result.ExitCode);
        Assert.Equal(new List<string> { "apply:database.schema", "apply:custody.kek", "rollback:database.schema" }, calls);
        Assert.Empty(result.AppliedStepIds);
        Assert.False(harness.Lock.Held);

        var row = Assert.Single(harness.Audit.Entries);
        Assert.Equal(SetupApplier.AppliedKind, row.Kind);
        var payload = PayloadOf(row);
        Assert.Equal("rolled_back", payload["outcome"]!.GetValue<string>());
        Assert.Equal("custody.kek", payload["failedStep"]!.GetValue<string>());
        Assert.Empty(harness.SettingAudit.Entries);
    }

    [Fact]
    public async Task Rollback_runs_all_applied_steps_in_reverse_order()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var applier = Applier(
            harness,
            Step("a.one", 10, calls),
            Step("a.two", 20, calls),
            Step("a.three", 30, calls),
            Step("a.four", 40, calls, StepBehaviour.Throw));

        var result = await applier.ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.ApplyFailedRolledBack, result.ExitCode);
        Assert.Equal(
            new List<string> {
                "apply:a.one", "apply:a.two", "apply:a.three", "apply:a.four",
                "rollback:a.three", "rollback:a.two", "rollback:a.one",
            },
            calls);
    }

    [Fact]
    public async Task Steps_run_in_order_not_in_registration_order()
    {
        var harness = new Harness();
        var calls = new List<string>();

        var result = await Applier(
            harness,
            Step("owner.identity", 30, calls),
            Step("database.schema", 10, calls),
            Step("custody.kek", 20, calls)).ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(new List<string> { "database.schema", "custody.kek", "owner.identity" }, result.AppliedStepIds);
        Assert.Equal(new List<string> { "apply:database.schema", "apply:custody.kek", "apply:owner.identity" }, calls);
    }

    [Fact]
    public async Task Success_writes_one_applied_row_and_one_setting_changed_row_per_changed_key()
    {
        var harness = new Harness();
        var calls = new List<string>();

        var result = await Applier(harness, Step("database.schema", 10, calls)).ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(new List<string> { "database.schema" }, result.AppliedStepIds);
        Assert.False(harness.Lock.Held);

        var row = Assert.Single(harness.Audit.Entries);
        Assert.Equal(SetupApplier.AppliedKind, row.Kind);
        Assert.Equal(_operator, row.Actor);
        var payload = PayloadOf(row);
        Assert.Equal(SetupPlanCanonicalizer.Hash(ValidPlan()), payload["planHash"]!.GetValue<string>());
        Assert.Equal("cli", payload["entryPath"]!.GetValue<string>());
        Assert.Equal("starter", payload["templateId"]!.GetValue<string>());
        Assert.Equal("1.0.0", payload["templateVersion"]!.GetValue<string>());
        Assert.Null(payload["kodoResult"]);
        Assert.Equal("applied", payload["outcome"]!.GetValue<string>());
        // The settings map plus the plan fields that map to one registry key each (network block, release channel).
        Assert.Equal(
            new List<string> { "host.shutdown_timeout_seconds", "host.status.bind", "host.status.port", "update.channel" },
            payload["changedSettingKeys"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(4, harness.SettingAudit.Entries.Count);
        Assert.Equal(["\"loopback\"", "8443", "\"beta\""], harness.SettingAudit.Entries.Where(e => e.Key != "host.shutdown_timeout_seconds").Select(e => e.NewValueJson));

        var setting = Assert.Single(harness.SettingAudit.Entries, e => e.Key == "host.shutdown_timeout_seconds");
        Assert.Equal(SettingWriteOutcome.Accepted, setting.Outcome);
        Assert.Equal(_operator, setting.ActorId);
        Assert.Null(setting.OldValueJson);
        Assert.Equal("20", setting.NewValueJson);
    }

    [Fact]
    public async Task Removed_setting_is_audited_with_a_null_new_value()
    {
        var harness = new Harness();
        var plan = SetupPlanLoader.Load(SetupFixtures.With("\"settings\": { \"host.shutdown_timeout_seconds\": 20 }", "\"settings\": {}"), RegistrySetupSettingCatalog.Known).Plan!;
        var state = new SetupStateSnapshot(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings.host.shutdown_timeout_seconds"] = "20",
        });

        var result = await Applier(harness, Step("database.schema", 10, [])).ApplyAsync(Request(plan, state: state), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        var setting = Assert.Single(harness.SettingAudit.Entries, e => e.Key == "host.shutdown_timeout_seconds");
        Assert.Equal("20", setting.OldValueJson);
        Assert.Equal("null", setting.NewValueJson);
    }

    [Fact]
    public async Task Applying_the_same_plan_twice_gives_the_same_step_results()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var applier = Applier(harness, Step("database.schema", 10, calls), Step("custody.kek", 20, calls));
        var request = Request(ValidPlan());

        var first = await applier.ApplyAsync(request, TestContext.Current.CancellationToken);
        var second = await applier.ApplyAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.ExitCode, second.ExitCode);
        Assert.Equal(first.AppliedStepIds, second.AppliedStepIds);
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(4, calls.Count(call => call.StartsWith("apply:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task With_no_steps_and_changes_the_apply_is_held_for_unapplied_sections_and_changes_nothing()
    {
        var harness = new Harness();
        var plan = ValidPlan();

        var result = await Applier(harness).ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SetupIssueSeverity.Hold, issue.Severity);
        Assert.Equal(SetupPlanSections.HoldReason, issue.Path);
        Assert.Equal(SetupPlanSections.Requested(plan).Select(section => section.Id), result.UnappliedSections);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Empty(harness.SettingAudit.Entries);
        var payload = PayloadOf(Assert.Single(harness.Audit.Entries));
        Assert.Equal("held_unapplied_sections", payload["outcome"]!.GetValue<string>());
        Assert.Equal(result.UnappliedSections, payload["unappliedSections"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Empty(payload["appliedSteps"]!.AsArray());
    }

    [Fact]
    public async Task With_no_steps_and_no_changes_the_apply_succeeds_with_an_empty_change_set()
    {
        var harness = new Harness();
        var plan = ValidPlan();
        var state = SetupStateSnapshot.FromNode(SetupPlanCanonicalizer.ToNode(plan));

        var result = await Applier(harness).ApplyAsync(Request(plan, state: state), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Empty(result.AppliedStepIds);
        Assert.Empty(result.UnappliedSections);
        Assert.Single(harness.Audit.Entries);
        Assert.Empty(harness.SettingAudit.Entries);
    }

    [Fact]
    public async Task Container_registration_with_no_steps_holds_the_same_way()
    {
        var harness = new Harness();
        var services = new ServiceCollection();
        services.AddSingleton<ISetupInstanceLock>(harness.Lock);
        services.AddSingleton<IAuditLog>(harness.Audit);
        services.AddSingleton<ISetupSettingAudit>(new SettingAuditSinkSetupAudit(harness.SettingAudit));
        services.AddSingleton<ISetupSettingCatalog>(RegistrySetupSettingCatalog.Known);
        services.AddSingleton<TimeProvider>(harness.Time);
        services.AddSetupApply([]);

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<SetupApplier>().ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Contains(result.Issues, issue => issue.Path == SetupPlanSections.HoldReason);
    }

    [Fact]
    public async Task A_full_plan_with_a_partial_step_is_refused_with_the_list_and_runs_no_step()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();
        var covered = SetupPlanSections.All.Select(section => section.Id).Where(id => id is not ("owner" or "backups")).ToList();

        var result = await Applier(harness, new RecordingStep("partial", 10, calls, StepBehaviour.Succeed) { Covers = covered })
            .ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Equal(["backups", "owner"], result.UnappliedSections.Order(StringComparer.Ordinal));
        Assert.Empty(calls);
        Assert.Empty(result.AppliedStepIds);
        Assert.Equal(0, harness.Lock.Acquisitions);
        var message = Assert.Single(result.Issues).Message;
        Assert.Contains("owner (doc 45 step 6)", message, StringComparison.Ordinal);
        Assert.Contains("backups (doc 45 step 8)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_limited_to_covered_sections_applies()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();
        var requested = SetupPlanSections.Requested(plan).Select(section => section.Id).ToList();

        var result = await Applier(harness, new RecordingStep("exact", 10, calls, StepBehaviour.Succeed) { Covers = requested })
            .ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(["apply:exact"], calls);
        Assert.Empty(result.UnappliedSections);
    }

    [Fact]
    public async Task A_section_the_plan_leaves_at_its_default_does_not_need_a_step()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();
        // The fixture leaves email, the scam-mail protocol and the update policy at their defaults.
        Assert.DoesNotContain(SetupPlanSections.Requested(plan), section => section.Id is "email" or "scamMailProtocol" or "install.updatePolicy");
        var covered = SetupPlanSections.All.Select(section => section.Id).Where(id => id is not ("email" or "scamMailProtocol" or "install.updatePolicy")).ToList();

        var result = await Applier(harness, new RecordingStep("most", 10, calls, StepBehaviour.Succeed) { Covers = covered })
            .ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
    }

    [Fact]
    public async Task Adding_a_covering_step_removes_the_section_from_the_list()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();
        var all = SetupPlanSections.All.Select(section => section.Id).ToList();
        var first = new RecordingStep("first", 10, calls, StepBehaviour.Succeed) { Covers = all.Where(id => id != "owner").ToList() };
        var second = new RecordingStep("second", 20, calls, StepBehaviour.Succeed) { Covers = ["owner"] };

        var before = Applier(harness, first);
        var after = Applier(harness, first, second);

        Assert.Equal(["owner"], before.UnappliedSectionsOf(plan).Select(section => section.Id));
        Assert.Empty(after.UnappliedSectionsOf(plan));
        var result = await after.ApplyAsync(Request(plan), TestContext.Current.CancellationToken);
        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(["apply:first", "apply:second"], calls);
    }

    [Fact]
    public async Task The_unapplied_list_and_its_audit_row_carry_no_secret_text()
    {
        var harness = new Harness();
        var plan = SetupPlanLoader.Load(SetupFixtures.Valid.Replace("secret://bazaar/providers/anthropic/key", "secret://bazaar/providers/" + SetupFixtures.Canary + "/key", StringComparison.Ordinal), RegistrySetupSettingCatalog.Known).Plan!;

        var result = await Applier(harness).ApplyAsync(Request(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Contains("models", result.UnappliedSections);
        Assert.DoesNotContain(SetupFixtures.Canary, string.Join('|', result.UnappliedSections) + string.Join('|', result.Issues.Select(issue => issue.Path + issue.Message)), StringComparison.Ordinal);
        Assert.DoesNotContain(SetupFixtures.Canary, Assert.Single(harness.Audit.Entries).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_canary_secret_never_appears_in_an_audit_row_or_a_result()
    {
        var harness = new Harness();
        var calls = new List<string>();

        var result = await Applier(
            harness,
            Step("database.schema", 10, calls),
            Step("custody.kek", 20, calls, StepBehaviour.Throw)).ApplyAsync(Request(ValidPlan()), TestContext.Current.CancellationToken);

        // The step threw an exception that carries the canary. Only its type may be recorded.
        var row = Assert.Single(harness.Audit.Entries);
        Assert.DoesNotContain(SetupFixtures.Canary, row.Payload, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupFixtures.Canary, string.Join("|", result.Issues.Select(issue => issue.Path + issue.Message)), StringComparison.Ordinal);
        Assert.Empty(harness.SettingAudit.Entries);
    }

    [Fact]
    public async Task Applied_row_records_the_template_id_version_and_hash_and_null_hash_when_no_template()
    {
        var harness = new Harness();
        var calls = new List<string>();
        var plan = ValidPlan();
        const string templateHash = "3f9c2a7e1b4d8f60a5c1e2d3b4a59687766554433221100ffeeddccbbaa9988";

        var withTemplate = new SetupApplyRequest(plan, SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Cli, _operator, "starter", "1.0.0", null, templateHash);
        await Applier(harness, Step("database.schema", 10, calls)).ApplyAsync(withTemplate, TestContext.Current.CancellationToken);

        var row = PayloadOf(Assert.Single(harness.Audit.Entries));
        Assert.Equal("starter", row["templateId"]!.GetValue<string>());
        Assert.Equal("1.0.0", row["templateVersion"]!.GetValue<string>());
        Assert.Equal(templateHash, row["templateHash"]!.GetValue<string>());

        var plainHarness = new Harness();
        var withoutTemplate = new SetupApplyRequest(plan, SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Cli, _operator);
        await Applier(plainHarness, Step("database.schema", 10, calls)).ApplyAsync(withoutTemplate, TestContext.Current.CancellationToken);

        var plainRow = PayloadOf(Assert.Single(plainHarness.Audit.Entries));
        Assert.Null(plainRow["templateHash"]);
        Assert.Null(plainRow["templateId"]);
    }

    /// <summary>Holds the fakes one test uses. Each test makes its own, so nothing is shared.</summary>
    private sealed class Harness
    {
        public FakeInstanceLock Lock { get; } = new();

        public FakeAuditLog Audit { get; } = new();

        public InMemorySettingAuditSink SettingAudit { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
    }

    public enum StepBehaviour
    {
        Succeed,
        ReportFailure,
        Throw,
    }

    /// <summary>Records every apply and rollback call in order. Throws a canary-bearing exception when asked to.</summary>
    private sealed class RecordingStep(string id, int order, List<string> calls, StepBehaviour behaviour) : ISetupStep
    {
        public string Id { get; } = id;

        public int Order { get; } = order;

        public IReadOnlyCollection<string> Covers { get; init; } = [.. SetupPlanSections.All.Select(section => section.Id)];

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
        {
            calls.Add("apply:" + Id);
            return behaviour switch
            {
                StepBehaviour.Succeed => Task.FromResult(SetupStepResult.Ok),
                StepBehaviour.ReportFailure => Task.FromResult(new SetupStepResult(false, "reported")),
                _ => throw new InvalidOperationException(SetupFixtures.Canary),
            };
        }

        public Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
        {
            calls.Add("rollback:" + Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>A lock that is held while a lease is open. Tests set <see cref="Held"/> to act as another process.</summary>
    private sealed class FakeInstanceLock : ISetupInstanceLock
    {
        public bool Held { get; set; }

        public int Acquisitions { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            if (Held)
            {
                throw new SetupInstanceLockHeldException();
            }

            Held = true;
            Acquisitions++;
            return Task.FromResult<IAsyncDisposable>(new Lease(this));
        }

        private sealed class Lease(FakeInstanceLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Held = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
