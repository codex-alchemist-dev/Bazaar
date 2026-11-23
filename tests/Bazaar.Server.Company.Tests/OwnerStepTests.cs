using System.Text.Json.Nodes;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Company.Tests;

/// <summary>The <c>owner.create</c> step over an in-memory store: what it records, that it never holds a secret, idempotency, refusal and rollback.</summary>
public sealed class OwnerStepTests
{
    private const string _bundle = "secret://bazaar/owner/recovery-bundle";

    private static (OwnerStep Step, MemoryOwnerStore Store, MemoryAudit Audit) Build()
    {
        var store = new MemoryOwnerStore();
        var audit = new MemoryAudit();
        return (new OwnerStep(store, audit), store, audit);
    }

    [Fact]
    public void It_covers_owner_and_recovery_but_not_the_backup_owners_and_runs_before_the_seeds()
    {
        var (step, _, _) = Build();

        Assert.Equal(["owner", "owner.recovery"], step.Covers);
        Assert.DoesNotContain("owner.backupOwners", step.Covers);
        // constitution.seed is 134 and firstrun.seed 135.
        Assert.True(step.Order < 134);
    }

    [Fact]
    public async Task Apply_creates_the_owner_and_records_the_choices_and_the_bundle_reference_only()
    {
        var (step, store, _) = Build();

        var result = await step.ApplyAsync(Plans.Plan(bundleRef: _bundle, printed: true), Plans.Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Secret Owner Name", store.Name);
        Assert.Equal(new OwnerRequest("Secret Owner Name", true, _bundle, true), store.Choices);
    }

    [Fact]
    public async Task The_audit_row_has_ids_and_flags_but_neither_the_name_nor_the_reference()
    {
        var (step, _, audit) = Build();

        await step.ApplyAsync(Plans.Plan(bundleRef: _bundle), Plans.Context("plan-hash-x"), TestContext.Current.CancellationToken);

        var row = Assert.Single(audit.Entries);
        Assert.Equal(OwnerStep.CreatedKind, row.Kind);
        var payload = (JsonObject)JsonNode.Parse(row.Payload)!;
        Assert.Equal("plan-hash-x", payload["planHash"]!.GetValue<string>());
        Assert.True(payload["ownerCreated"]!.GetValue<bool>());
        Assert.True(payload["recoveryBundleRecorded"]!.GetValue<bool>());
        Assert.DoesNotContain("Secret Owner Name", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(_bundle, row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_run_creates_nothing_and_writes_no_audit_row()
    {
        var (step, _, audit) = Build();
        var plan = Plans.Plan();
        await step.ApplyAsync(plan, Plans.Context("a"), TestContext.Current.CancellationToken);

        var second = await step.ApplyAsync(plan, Plans.Context("b"), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task An_owner_with_another_name_is_refused_without_naming_either()
    {
        var (step, store, audit) = Build();
        store.Name = "Somebody Else";

        var result = await step.ApplyAsync(Plans.Plan(), Plans.Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(audit.Entries);
        Assert.DoesNotContain("Somebody Else", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Owner Name", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_audit_write_removes_what_the_step_created()
    {
        var (step, store, audit) = Build();
        audit.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => step.ApplyAsync(Plans.Plan(), Plans.Context(), TestContext.Current.CancellationToken));

        Assert.Null(store.Name);
        Assert.Null(store.Choices);
    }

    [Fact]
    public async Task Rollback_removes_what_this_run_created_and_writes_a_row()
    {
        var (step, store, audit) = Build();
        var plan = Plans.Plan();
        await step.ApplyAsync(plan, Plans.Context(), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Plans.Context(), TestContext.Current.CancellationToken);

        Assert.Null(store.Name);
        Assert.Null(store.Choices);
        Assert.Equal([OwnerStep.CreatedKind, OwnerStep.RolledBackKind], audit.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task Rollback_leaves_an_owner_an_earlier_run_created()
    {
        var (step, store, _) = Build();
        var plan = Plans.Plan();
        await step.ApplyAsync(plan, Plans.Context("first"), TestContext.Current.CancellationToken);
        await step.ApplyAsync(plan, Plans.Context("second"), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Plans.Context("second"), TestContext.Current.CancellationToken);

        Assert.NotNull(store.Name);
    }
}
