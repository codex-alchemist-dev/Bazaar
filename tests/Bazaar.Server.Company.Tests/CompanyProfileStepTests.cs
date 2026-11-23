using System.Text.Json.Nodes;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Company.Tests;

/// <summary>The <c>company.profile</c> step over an in-memory store: create, idempotency, refusal, audit and rollback.</summary>
public sealed class CompanyProfileStepTests
{
    private static (CompanyProfileStep Step, MemoryProfileStore Store, MemoryAudit Audit) Build()
    {
        var store = new MemoryProfileStore();
        var audit = new MemoryAudit();
        return (new CompanyProfileStep(store, audit), store, audit);
    }

    [Fact]
    public void It_covers_the_install_identity_section_and_runs_before_the_owner_and_the_seeds()
    {
        var (step, _, _) = Build();

        Assert.Equal(["install.identity"], step.Covers);
        Assert.True(step.Order < OwnerStep.StepOrder);
        Assert.Equal("company.profile", step.Id);
    }

    [Fact]
    public async Task Apply_creates_the_profile_from_the_install_section()
    {
        var (step, store, _) = Build();

        var result = await step.ApplyAsync(Plans.Plan(), Plans.Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new CompanyProfileRequest("Example Studio", "example-studio", "en", "Europe/London"), store.Held);
    }

    [Fact]
    public async Task The_audit_row_has_the_hash_the_entry_path_and_the_slug()
    {
        var (step, _, audit) = Build();

        await step.ApplyAsync(Plans.Plan(), Plans.Context("plan-hash-x"), TestContext.Current.CancellationToken);

        var row = Assert.Single(audit.Entries);
        Assert.Equal(CompanyProfileStep.CreatedKind, row.Kind);
        Assert.Equal(Plans.Operator, row.Actor);
        var payload = (JsonObject)JsonNode.Parse(row.Payload)!;
        Assert.Equal("plan-hash-x", payload["planHash"]!.GetValue<string>());
        Assert.Equal("cli", payload["entryPath"]!.GetValue<string>());
        Assert.Equal("example-studio", payload["companySlug"]!.GetValue<string>());
        Assert.DoesNotContain("Example Studio", row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_run_creates_nothing_and_writes_no_audit_row()
    {
        var (step, _, audit) = Build();
        await step.ApplyAsync(Plans.Plan(), Plans.Context("a"), TestContext.Current.CancellationToken);

        var second = await step.ApplyAsync(Plans.Plan(), Plans.Context("b"), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task A_company_with_other_values_is_refused_and_nothing_is_audited()
    {
        var (step, store, audit) = Build();
        store.Held = new CompanyProfileRequest("Other", "other", "en", "UTC");

        var result = await step.ApplyAsync(Plans.Plan(), Plans.Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(audit.Entries);
        Assert.Equal("other", store.Held.Slug);
    }

    [Fact]
    public async Task A_failed_audit_write_removes_the_row_the_step_created()
    {
        var (step, store, audit) = Build();
        audit.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => step.ApplyAsync(Plans.Plan(), Plans.Context(), TestContext.Current.CancellationToken));

        Assert.Null(store.Held);
    }

    [Fact]
    public async Task Rollback_removes_what_this_run_created_and_writes_a_row()
    {
        var (step, store, audit) = Build();
        var plan = Plans.Plan();
        await step.ApplyAsync(plan, Plans.Context(), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Plans.Context(), TestContext.Current.CancellationToken);

        Assert.Null(store.Held);
        Assert.Equal([CompanyProfileStep.CreatedKind, CompanyProfileStep.RolledBackKind], audit.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task Rollback_leaves_a_profile_an_earlier_run_created()
    {
        var (step, store, _) = Build();
        var plan = Plans.Plan();
        await step.ApplyAsync(plan, Plans.Context("first"), TestContext.Current.CancellationToken);
        await step.ApplyAsync(plan, Plans.Context("second"), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Plans.Context("second"), TestContext.Current.CancellationToken);

        Assert.NotNull(store.Held);
    }
}
