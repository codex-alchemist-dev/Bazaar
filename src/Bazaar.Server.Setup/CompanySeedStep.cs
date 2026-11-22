using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Setup;

/// <summary>
/// The setup step that creates the company, its owner and its first agents (doc 45 steps 1 and 6, section 1.3a). It applies three
/// plan sections: <c>install.identity</c> (name, slug, locale, time zone), <c>owner</c> (display name, second-factor requirement) and
/// <c>agents</c>. The recovery half of the owner step (<c>owner.recovery</c>) and the passkey ceremony are not applied here; the plan
/// still holds an apply for <c>owner.recovery</c> until a step covers it.
/// </summary>
/// <remarks>
/// Stores go through <see cref="ISetupCompanySeeder"/>. The step is idempotent (a second run creates nothing and writes no audit row),
/// refuses a conflicting state instead of overwriting it, and rolls back only what this run created. It writes one audit row,
/// <c>setup.company.seeded</c>, when it created something, and <c>setup.company.rolled_back</c> on rollback. Rows hold the plan hash,
/// the entry path, the company slug and ids and counts; never the owner's name (personal data) or a secret. If the audit write fails
/// the step removes what it created before it lets the failure through, because a failed step is not rolled back by the applier.
/// Agents are created in the <c>created</c> state and never activated: the Constitution must be accepted first (doc 24).
/// </remarks>
public sealed class CompanySeedStep(ISetupCompanySeeder seeder, IAuditLog audit, TimeProvider? time = null) : ISetupStep
{
    /// <summary>The step id written to the apply audit row.</summary>
    public const string StepId = "company.seed";

    /// <summary>Doc 45 step 6 is order 600; the seed runs at 650, after any owner-ceremony step and before models (900).</summary>
    public const int StepOrder = 650;

    /// <summary>The audit kind of the row written when the step created something.</summary>
    public const string SeededKind = "setup.company.seeded";

    /// <summary>The audit kind of the row written when the step is rolled back.</summary>
    public const string RolledBackKind = "setup.company.rolled_back";

    /// <summary>The plan sections this step applies.</summary>
    public static readonly IReadOnlyList<string> CoveredSections = ["install.identity", "owner", "agents"];

    private readonly ISetupCompanySeeder _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
    private readonly IAuditLog _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // What this run created, by plan hash, for the rollback of the same apply. An apply holds the instance lock, so there is one at a time.
    private readonly ConcurrentDictionary<string, CompanySeedOutcome> _created = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => CoveredSections;

    /// <summary>Builds the seed request from a plan. Public so a test and a dry run read the same mapping.</summary>
    public static CompanySeedRequest RequestFrom(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new CompanySeedRequest(
            plan.Install.CompanyName,
            plan.Install.CompanySlug,
            plan.Install.Locale,
            plan.Install.Timezone,
            plan.Owner.DisplayName,
            plan.Owner.RequireSecondFactor,
            [.. (plan.Agents ?? []).Select(agent => agent.Name)]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DescribeAsync(SetupPlan plan, CancellationToken cancellationToken)
    {
        var preview = await _seeder.PreviewAsync(RequestFrom(plan), cancellationToken).ConfigureAwait(false);
        if (preview.Conflict is not null)
        {
            return [$"refused: {preview.Conflict}"];
        }

        var lines = new List<string>
        {
            preview.CompanyExists ? "company: already present, left as it is" : $"company: create '{plan.Install.CompanySlug}'",
            preview.OwnerExists ? "owner: already present, left as it is" : "owner: create the owner identity",
        };
        lines.AddRange(preview.AgentsToCreate.Select(name => $"agent: create '{name}' (not active until the Constitution is accepted)"));
        lines.AddRange(preview.AgentsExisting.Select(name => $"agent: '{name}' already present, left as it is"));
        return lines;
    }

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var result = await _seeder.SeedAsync(RequestFrom(plan), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Outcome is null)
        {
            return new SetupStepResult(false, result.Refusal ?? "the company seed was refused.");
        }

        var outcome = result.Outcome;
        if (outcome.IsEmpty)
        {
            return new SetupStepResult(true, "company, owner and agents were already present.");
        }

        try
        {
            await _audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, SeededKind, Payload(context, outcome, result.AgentsFound).ToJsonString()),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The applier does not roll back the step that failed, so undo this run's writes here, then let the failure through.
            await _seeder.RollbackAsync(outcome, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _created[context.PlanHash] = outcome;
        return new SetupStepResult(true, $"created: company {(outcome.CompanyCreated ? 1 : 0)}, owner {(outcome.OwnerCreatedId is null ? 0 : 1)}, agents {outcome.AgentsCreated.Count}.");
    }

    /// <inheritdoc />
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_created.TryRemove(context.PlanHash, out var outcome))
        {
            return;
        }

        await _seeder.RollbackAsync(outcome, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, RolledBackKind, Payload(context, outcome, []).ToJsonString()),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Payload(SetupApplyContext context, CompanySeedOutcome outcome, IReadOnlyList<SeededAgent> found) => new()
    {
        ["planHash"] = context.PlanHash,
        ["entryPath"] = context.EntryPath.ToString().ToLowerInvariant(),
        ["companySlug"] = outcome.Slug,
        ["companyCreated"] = outcome.CompanyCreated,
        ["ownerCreated"] = outcome.OwnerCreatedId is not null,
        ["ownerId"] = outcome.OwnerCreatedId?.ToString(),
        ["agentsCreated"] = new JsonArray([.. outcome.AgentsCreated.Select(agent => (JsonNode?)new JsonObject { ["id"] = agent.Id.ToString(), ["name"] = agent.Name })]),
        ["agentsAlreadyPresent"] = found.Count,
    };
}
