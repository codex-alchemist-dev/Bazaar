using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Company;

/// <summary>
/// The setup step that creates the owner (doc 45 step 6, section 1.3a), so the first-run seed finds an owner row. It applies two plan
/// sections: <c>owner</c> (display name, second-factor requirement) and <c>owner.recovery</c> (the recovery bundle reference and the
/// printed-codes flag). The backup owners are a third section, <c>owner.backupOwners</c>, which no step applies yet.
/// </summary>
/// <remarks>
/// It creates the owner <c>human</c> row and records the owner's choices. No passkey, recovery bundle, code or key is created or read:
/// the bundle reference is a <c>secret://bazaar/</c> string kept as given, and the owner enrols a second factor through the sign-in
/// page. Idempotent, refuses a conflict, rolls back only what this run created. One audit row, <c>setup.owner.created</c>: plan hash,
/// entry path, owner id and flags; never the display name or the reference. A failed audit write removes the step's own rows first.
/// </remarks>
public sealed class OwnerStep(IOwnerStore store, IAuditLog audit, TimeProvider? time = null) : ISetupStep
{
    /// <summary>The step id written to the apply audit row.</summary>
    public const string StepId = "owner.create";

    /// <summary>Doc 45 step 6; the first-run seed comments rely on this being 60, before the Constitution (134) and the first run (135).</summary>
    public const int StepOrder = 60;

    /// <summary>The audit kind of the row written when the step created something.</summary>
    public const string CreatedKind = "setup.owner.created";

    /// <summary>The audit kind of the row written when the step is rolled back.</summary>
    public const string RolledBackKind = "setup.owner.rolled_back";

    /// <summary>The plan sections this step applies.</summary>
    public static readonly IReadOnlyList<string> CoveredSections = ["owner", "owner.recovery"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, OwnerOutcome> _created = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => CoveredSections;

    /// <summary>Builds the store request from a plan. Public so a test reads the same mapping.</summary>
    public static OwnerRequest RequestFrom(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new OwnerRequest(plan.Owner.DisplayName, plan.Owner.RequireSecondFactor, plan.Owner.Recovery.BundleRef, plan.Owner.Recovery.PrintedCodes);
    }

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var request = RequestFrom(plan);
        var result = await store.EnsureAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Outcome is not { } outcome)
        {
            return new SetupStepResult(false, result.Refusal ?? "the owner step was refused.");
        }

        if (outcome.IsEmpty)
        {
            return new SetupStepResult(true, "the owner was already present.");
        }

        try
        {
            await audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, CreatedKind, Payload(context, outcome, request).ToJsonString()),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.RollbackAsync(outcome, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _created[context.PlanHash] = outcome;
        return new SetupStepResult(true, outcome.OwnerCreatedId is null ? "the owner choices were recorded." : "the owner was created.");
    }

    /// <inheritdoc />
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (!_created.TryRemove(context.PlanHash, out var outcome))
        {
            return;
        }

        await store.RollbackAsync(outcome, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, RolledBackKind, Payload(context, outcome, RequestFrom(plan)).ToJsonString()),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Payload(SetupApplyContext context, OwnerOutcome outcome, OwnerRequest request) => new()
    {
        ["planHash"] = context.PlanHash,
        ["entryPath"] = context.EntryPath.ToString().ToLowerInvariant(),
        ["ownerCreated"] = outcome.OwnerCreatedId is not null,
        ["ownerId"] = outcome.OwnerCreatedId?.ToString(),
        ["choicesCreated"] = outcome.ChoicesCreated,
        ["requireSecondFactor"] = request.RequireSecondFactor,
        ["recoveryBundleRecorded"] = request.RecoveryBundleRef is not null,
        ["printedCodes"] = request.PrintedCodes,
    };
}
