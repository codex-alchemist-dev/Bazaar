using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>
/// Thinking GC rules (docs/design/01-gc-data-model.md, Thinking GCs [DECIDED, round 3]). Pure: no SQL.
/// </summary>
public static class ThinkingGcRules
{
    /// <summary>
    /// Plans the Thinking GC for an agent under a working GC. The returned Gc has type <c>thinking</c> and
    /// ParentGcId = <paramref name="workingGc"/>.GcId. The returned Member row is the agent's member row with
    /// JoinedSeq 0 as a placeholder: the writer sets the real seq. The plan has no event; a creation notice is the writer's call.
    /// </summary>
    /// <param name="workingGc">The GC the agent works in.</param>
    /// <param name="workingMembers">Membership rows of the working GC.</param>
    /// <param name="agentId">The agent that owns the Thinking GC.</param>
    /// <param name="thinkingGcId">Id the writer assigns to the new GC.</param>
    /// <param name="agentHasThinkingGc">True when the writer finds a Thinking GC already under this working GC for this agent.</param>
    public static GcPlanResult Create(
        GcRecord workingGc,
        IReadOnlyCollection<GcMemberRecord> workingMembers,
        Guid agentId,
        Guid thinkingGcId,
        bool agentHasThinkingGc)
    {
        ArgumentNullException.ThrowIfNull(workingGc);
        ArgumentNullException.ThrowIfNull(workingMembers);

        if (workingGc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.Archived);
        }

        if (GcTypeKeys.IsThinking(workingGc))
        {
            return GcPlanResult.Refused(GcPlanRefusal.WorkingGcIsThinking);
        }

        var row = GcAccessPolicy.CurrentRow(workingGc, workingMembers, agentId);
        if (!GcAccessPolicy.IsActive(row) || row.Role != GcMemberRole.Member || row.Kind != PrincipalKind.Agent)
        {
            return GcPlanResult.Refused(GcPlanRefusal.AgentNotWorkingMember);
        }

        if (agentHasThinkingGc)
        {
            return GcPlanResult.Refused(GcPlanRefusal.AgentAlreadyHasThinkingGc);
        }

        var gc = new GcRecord(thinkingGcId, workingGc.GcId, GcTypeKeys.Thinking, null);
        var member = new GcMemberRecord(thinkingGcId, agentId, PrincipalKind.Agent, GcMemberRole.Member, 0, null);
        return GcPlanResult.Ok(gc, member, null);
    }

    /// <summary>
    /// The owning agent of a Thinking GC: the agent-kind row with the lowest JoinedSeq (ties broken by principal id).
    /// The row counts whether or not it has left, because ownership is identity. Null for other types or when no agent row exists.
    /// </summary>
    public static Guid? OwningAgentId(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);

        if (!GcTypeKeys.IsThinking(gc))
        {
            return null;
        }

        return members
            .Where(m => m.GcId == gc.GcId && m.Kind == PrincipalKind.Agent)
            .OrderBy(m => m.JoinedSeq)
            .ThenBy(m => m.PrincipalId)
            .Select(m => (Guid?)m.PrincipalId)
            .FirstOrDefault();
    }

    /// <summary>
    /// True when a context builder must not load this GC into the requesting agent's context. Any Thinking GC
    /// not owned by that agent is excluded. If no owning agent can be found, the GC is excluded for every agent (fail closed).
    /// </summary>
    public static bool IsExcludedFromContext(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, Guid requestingAgentId)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);

        if (!GcTypeKeys.IsThinking(gc))
        {
            return false;
        }

        return OwningAgentId(gc, members) != requestingAgentId;
    }

    /// <summary>
    /// True when the GC can wake an agent. Thinking GCs never wake anyone. Archived GCs do not wake anyone either
    /// (docs/design/01-gc-data-model.md, Attachments, embeds, archiving).
    /// </summary>
    public static bool IsWakeTarget(GcRecord gc)
    {
        ArgumentNullException.ThrowIfNull(gc);
        return !GcTypeKeys.IsThinking(gc) && gc.ArchivedAt is null;
    }
}
