using System.Collections.Concurrent;

namespace Bazaar.Server.Setup.FirstRun;

/// <summary>
/// The first-run seed (doc 45 section 1.15, PROPOSED): the first group chat, the default agent, and the owner's Constitution
/// acceptance. Idempotent: the store finds rows it already made. The agent is active only after acceptance. A step never writes a
/// name or secret into its result; messages name keys and outcomes only.
/// </summary>
public sealed class FirstRunSeedStep(
    IFirstRunOwner owner,
    IFirstRunStore store,
    IConstitutionAcceptance acceptance,
    IDefaultAgentRoutes routes,
    IFirstRunAudit audit) : ISetupStep
{
    /// <summary>Stable id, written to the audit row.</summary>
    public const string StepId = "firstrun.seed";

    /// <summary>After the owner step (60) and the company source step (130), before modules.</summary>
    public const int StepOrder = 135;

    // What this run created, by plan hash, so a rollback removes only its own rows and never a seed an earlier apply made.
    private readonly ConcurrentDictionary<string, Created> _created = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var section = plan.FirstRun;
        if (!section.SeedGeneralGc && !section.SeedDefaultAgent) return SetupStepResult.Ok;

        if (await owner.GetOwnerPrincipalIdAsync(cancellationToken) is not { } ownerId)
            return new SetupStepResult(false, "No owner exists yet. The owner step must run before the first-run seed.");

        var made = new Created();
        _created[context.PlanHash] = made;

        SeededGc? gc = null;
        if (section.SeedGeneralGc)
        {
            gc = await store.EnsureGeneralGcAsync(section.GeneralGcTitle, ownerId, cancellationToken);
            if (gc.Created) made.GcId = gc.GcId;
        }

        SeededAgent? agent = null;
        string? note = null;
        if (section.SeedDefaultAgent)
        {
            var whitelist = await routes.GetWhitelistAsync(section.DefaultAgentSources, cancellationToken);
            agent = await store.EnsureDefaultAgentAsync(section.DefaultAgentName, section.DefaultAgentPurpose, gc?.GcId, whitelist, cancellationToken);
            if (agent.Created) made.AgentId = agent.AgentId;
            if (whitelist.Count == 0) note = "No Claude model source is available, so the default agent has no route and cannot answer yet.";
        }

        var accepted = section.AcceptConstitution && await acceptance.RecordOwnerAcceptanceAsync(ownerId, cancellationToken);
        if (agent is not null && accepted) await store.ActivateAgentAsync(agent.AgentId, cancellationToken);

        await audit.WriteSeededAsync(gc?.GcId, agent?.AgentId, accepted, context.OperatorIdentity, cancellationToken);

        if (agent is not null && !accepted) note ??= "The default agent waits for the owner to accept the Constitution.";
        return new SetupStepResult(true, note);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_created.TryRemove(context.PlanHash, out var made)) return;
        if (made.AgentId is { } agentId) await store.RemoveAgentAsync(agentId, cancellationToken);
        if (made.GcId is { } gcId) await store.ArchiveGcAsync(gcId, cancellationToken);
    }

    private sealed class Created
    {
        public Guid? GcId { get; set; }

        public Guid? AgentId { get; set; }
    }
}
