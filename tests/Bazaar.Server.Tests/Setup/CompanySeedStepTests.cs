using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The company seed step (doc 45 steps 1 and 6, section 1.3a) over an in-memory seeder: what it creates, that a second run creates
/// nothing, refusals, the audit rows (no owner name), rollback, the dry-run lines and the plan field. The Postgres seeder is tested in
/// Bazaar.Server.Company.Tests.
/// </summary>
public sealed class CompanySeedStepTests
{
    private const string _operator = "operator-test";

    private static SetupPlan PlanWith(string? agentsJson)
    {
        var json = agentsJson is null ? SetupFixtures.Valid : SetupFixtures.With("\"modules\":", $"\"agents\": {agentsJson}, \"modules\":");
        var load = SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);
        Assert.True(load.Plan is not null, string.Join("; ", load.Issues.Select(issue => issue.Path + " " + issue.Message)));
        return load.Plan;
    }

    private static SetupApplyContext Context(string hash = "hash-1") => new(hash, SetupEntryPath.Cli, _operator, null, null, SetupStateSnapshot.Empty, []);

    private static (CompanySeedStep Step, MemorySeeder Seeder, MemoryAudit Audit) Build()
    {
        var seeder = new MemorySeeder();
        var audit = new MemoryAudit();
        return (new CompanySeedStep(seeder, audit, new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero))), seeder, audit);
    }

    [Fact]
    public async Task Apply_creates_the_company_the_owner_and_each_agent()
    {
        var (step, seeder, _) = Build();

        var result = await step.ApplyAsync(PlanWith("""[{ "name": "Charles" }, { "name": "Ada" }]"""), Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("example-studio", seeder.Slug);
        Assert.Equal("Owner", seeder.OwnerName);
        Assert.Equal(["Charles", "Ada"], seeder.Agents.Select(agent => agent.Name));
        Assert.True(seeder.SecondFactor);
    }

    [Fact]
    public async Task A_second_run_creates_nothing_and_writes_no_audit_row()
    {
        var (step, seeder, audit) = Build();
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        await step.ApplyAsync(plan, Context("a"), TestContext.Current.CancellationToken);

        var second = await step.ApplyAsync(plan, Context("b"), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Single(seeder.Agents);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task The_audit_row_has_ids_and_names_of_agents_but_not_the_owner_name()
    {
        var (step, _, audit) = Build();

        await step.ApplyAsync(PlanWith("""[{ "name": "Charles" }]"""), Context("plan-hash-x"), TestContext.Current.CancellationToken);

        var row = Assert.Single(audit.Entries);
        Assert.Equal(CompanySeedStep.SeededKind, row.Kind);
        Assert.Equal(_operator, row.Actor);
        var payload = (JsonObject)JsonNode.Parse(row.Payload)!;
        Assert.Equal("plan-hash-x", payload["planHash"]!.GetValue<string>());
        Assert.Equal("cli", payload["entryPath"]!.GetValue<string>());
        Assert.Equal("example-studio", payload["companySlug"]!.GetValue<string>());
        Assert.True(payload["ownerCreated"]!.GetValue<bool>());
        Assert.Equal("Charles", payload["agentsCreated"]![0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("Owner", row.Payload.Replace("ownerCreated", "", StringComparison.Ordinal).Replace("ownerId", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflicting_company_is_refused_and_nothing_is_created()
    {
        var (step, seeder, audit) = Build();
        seeder.Slug = "another-company";

        var result = await step.ApplyAsync(PlanWith("""[{ "name": "Charles" }]"""), Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(seeder.Agents);
        Assert.Empty(audit.Entries);
        Assert.DoesNotContain("another-company", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_removes_what_this_run_created_and_writes_a_row()
    {
        var (step, seeder, audit) = Build();
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        await step.ApplyAsync(plan, Context(), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Context(), TestContext.Current.CancellationToken);

        Assert.Null(seeder.Slug);
        Assert.Null(seeder.OwnerName);
        Assert.Empty(seeder.Agents);
        Assert.Equal([CompanySeedStep.SeededKind, CompanySeedStep.RolledBackKind], audit.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task Rollback_leaves_what_an_earlier_run_created()
    {
        var (step, seeder, _) = Build();
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        await step.ApplyAsync(plan, Context("first"), TestContext.Current.CancellationToken);
        await step.ApplyAsync(plan, Context("second"), TestContext.Current.CancellationToken);

        await step.RollbackAsync(plan, Context("second"), TestContext.Current.CancellationToken);

        Assert.Equal("example-studio", seeder.Slug);
        Assert.Single(seeder.Agents);
    }

    [Fact]
    public async Task A_failing_audit_write_removes_the_created_rows_and_lets_the_failure_through()
    {
        var (step, seeder, audit) = Build();
        audit.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => step.ApplyAsync(PlanWith("""[{ "name": "Charles" }]"""), Context(), TestContext.Current.CancellationToken));

        Assert.Null(seeder.Slug);
        Assert.Empty(seeder.Agents);
    }

    [Fact]
    public async Task Describe_lists_the_changes_and_then_the_already_present_parts()
    {
        var (step, _, _) = Build();
        var plan = PlanWith("""[{ "name": "Charles" }]""");

        var before = await step.DescribeAsync(plan, TestContext.Current.CancellationToken);
        await step.ApplyAsync(plan, Context(), TestContext.Current.CancellationToken);
        var after = await step.DescribeAsync(plan, TestContext.Current.CancellationToken);

        Assert.Contains("company: create 'example-studio'", before);
        Assert.Contains(before, line => line.StartsWith("agent: create 'Charles'", StringComparison.Ordinal));
        Assert.Contains(after, line => line.Contains("already present", StringComparison.Ordinal) && line.StartsWith("company", StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_applier_runs_the_step_rolls_it_back_when_a_later_step_fails_and_summarises_the_dry_run()
    {
        var seeder = new MemorySeeder();
        var audit = new MemoryAudit();
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        var step = new CompanySeedStep(seeder, audit);
        var later = new CoverAllStep(fail: true);
        var applier = new SetupApplier(new FreeLock(), [step, later], audit, new SettingAuditSinkSetupAudit(new InMemorySettingAuditSink()), new FakeTimeProvider(), RegistrySetupSettingCatalog.Known);

        var summary = await applier.DescribeStepsAsync(plan, TestContext.Current.CancellationToken);
        var result = await applier.ApplyAsync(
            new SetupApplyRequest(plan, SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Api, _operator), TestContext.Current.CancellationToken);

        Assert.Contains("company.seed: company: create 'example-studio'", summary);
        Assert.Equal(SetupExitCode.ApplyFailedRolledBack, result.ExitCode);
        Assert.Null(seeder.Slug);
        Assert.Empty(seeder.Agents);
        Assert.Contains(audit.Entries, entry => entry.Kind == CompanySeedStep.RolledBackKind);
    }

    [Fact]
    public async Task The_applier_applies_a_plan_whose_sections_the_step_and_a_second_step_cover()
    {
        var seeder = new MemorySeeder();
        var audit = new MemoryAudit();
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        var applier = new SetupApplier(new FreeLock(), [new CompanySeedStep(seeder, audit), new CoverAllStep(fail: false)], audit, new SettingAuditSinkSetupAudit(new InMemorySettingAuditSink()), new FakeTimeProvider(), RegistrySetupSettingCatalog.Known);

        var result = await applier.ApplyAsync(new SetupApplyRequest(plan, SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Api, _operator), TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Contains(CompanySeedStep.StepId, result.AppliedStepIds);
        Assert.Single(seeder.Agents);
    }

    [Fact]
    public void The_step_covers_identity_owner_and_agents_but_not_the_recovery_half()
    {
        var step = new CompanySeedStep(new MemorySeeder(), new MemoryAudit());

        Assert.Equal(["install.identity", "owner", "agents"], step.Covers);
        Assert.DoesNotContain("owner.recovery", step.Covers);
        Assert.True(SetupPlanSections.IsKnown("agents"));
        Assert.True(SetupPlanSections.IsKnown("owner.recovery"));
    }

    [Fact]
    public void Printed_recovery_codes_keep_the_owner_recovery_section_unapplied()
    {
        var step = new CompanySeedStep(new MemorySeeder(), new MemoryAudit());
        var plan = PlanWith("""[{ "name": "Charles" }]""");
        var withCodes = SetupPlanLoader.Load(SetupFixtures.With("\"printedCodes\": false", "\"printedCodes\": true"), RegistrySetupSettingCatalog.Known).Plan!;

        Assert.DoesNotContain(SetupPlanSections.Unapplied(plan, step.Covers), section => section.Id is "owner" or "owner.recovery" or "install.identity" or "agents");
        Assert.Contains(SetupPlanSections.Unapplied(withCodes, step.Covers), section => section.Id == "owner.recovery");
    }

    [Fact]
    public void A_plan_without_agents_keeps_its_hash_and_one_with_agents_has_another()
    {
        var without = PlanWith(null);
        var with = PlanWith("""[{ "name": "Charles" }]""");

        Assert.DoesNotContain("agents", System.Text.Encoding.UTF8.GetString(SetupPlanCanonicalizer.ToCanonicalUtf8(without)), StringComparison.Ordinal);
        Assert.NotEqual(SetupPlanCanonicalizer.Hash(without), SetupPlanCanonicalizer.Hash(with));
    }

    [Theory]
    [InlineData("""[{ "name": "" }]""", "agents[0].name")]
    [InlineData("""[{ "name": " Ada" }]""", "agents[0].name")]
    [InlineData("""[{ "name": "Ada" }, { "name": "ADA" }]""", "agents[1].name")]
    [InlineData("""[{ "name": "Ada\u0007" }]""", "agents[0].name")]
    public void Bad_agent_names_are_refused_by_path(string agentsJson, string path)
    {
        var json = SetupFixtures.With("\"modules\":", $"\"agents\": {agentsJson}, \"modules\":");

        var load = SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

        Assert.Contains(load.Issues, issue => issue.Path == path && issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void More_than_sixteen_agents_are_refused()
    {
        var names = string.Join(",", Enumerable.Range(0, 17).Select(i => $$"""{ "name": "Agent{{i}}" }"""));
        var json = SetupFixtures.With("\"modules\":", $"\"agents\": [{names}], \"modules\":");

        var load = SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

        Assert.Contains(load.Issues, issue => issue.Path == "agents" && issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void An_unknown_key_inside_an_agent_is_refused()
    {
        var json = SetupFixtures.With("\"modules\":", "\"agents\": [{ \"name\": \"Ada\", \"soul\": \"x\" }], \"modules\":");

        var load = SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

        Assert.Contains(load.Issues, issue => issue.Path.StartsWith("agents", StringComparison.Ordinal));
    }

    private sealed class MemoryAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("audit down");
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class FreeLock : ISetupInstanceLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Covers every section so the applier does not hold the plan; optionally fails, to drive the rollback of the seed step.</summary>
    private sealed class CoverAllStep(bool fail) : ISetupStep
    {
        public string Id => "everything.else";

        public int Order => 1000;

        public IReadOnlyCollection<string> Covers { get; } = [.. SetupPlanSections.All.Select(section => section.Id)];

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(fail ? new SetupStepResult(false, "later step failed") : SetupStepResult.Ok);
    }

    /// <summary>The seeder's rules in memory: idempotent, refuses conflicts, rolls back only what an outcome names.</summary>
    private sealed class MemorySeeder : ISetupCompanySeeder
    {
        public string? Slug { get; set; }

        public string? OwnerName { get; private set; }

        public bool SecondFactor { get; private set; }

        public List<SeededAgent> Agents { get; } = [];

        public Task<CompanySeedPreview> PreviewAsync(CompanySeedRequest request, CancellationToken cancellationToken)
        {
            var existing = request.AgentNames.Where(name => Agents.Any(agent => Same(agent.Name, name))).ToList();
            return Task.FromResult(new CompanySeedPreview(Slug is not null, OwnerName is not null, [.. request.AgentNames.Except(existing)], existing, Conflict(request)));
        }

        public Task<CompanySeedResult> SeedAsync(CompanySeedRequest request, CancellationToken cancellationToken)
        {
            if (Conflict(request) is { } conflict)
            {
                return Task.FromResult(CompanySeedResult.Refused(conflict));
            }

            var companyCreated = Slug is null;
            Slug = request.Slug;
            Guid? ownerId = null;
            if (OwnerName is null)
            {
                OwnerName = request.OwnerDisplayName;
                ownerId = Guid.CreateVersion7();
            }

            SecondFactor = request.RequireSecondFactor;
            var created = new List<SeededAgent>();
            var found = new List<SeededAgent>();
            foreach (var name in request.AgentNames)
            {
                var existing = Agents.Find(agent => Same(agent.Name, name));
                if (existing is not null)
                {
                    found.Add(existing);
                    continue;
                }

                var agent = new SeededAgent(Guid.CreateVersion7(), name);
                Agents.Add(agent);
                created.Add(agent);
            }

            return Task.FromResult(new CompanySeedResult(new CompanySeedOutcome(request.Slug, companyCreated, ownerId, created), null, found));
        }

        public Task RollbackAsync(CompanySeedOutcome outcome, CancellationToken cancellationToken)
        {
            Agents.RemoveAll(agent => outcome.AgentsCreated.Any(created => created.Id == agent.Id));
            if (outcome.OwnerCreatedId is not null)
            {
                OwnerName = null;
            }

            if (outcome.CompanyCreated)
            {
                Slug = null;
            }

            return Task.CompletedTask;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private string? Conflict(CompanySeedRequest request) =>
            Slug is not null && Slug != request.Slug ? "the install already has a company with other values; one company per install, and setup does not edit it." : null;
    }
}
