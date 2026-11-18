namespace Bazaar.Server.Modules.Releases;

/// <summary>One install and the ring it sits in, with the release version it runs (doc 49 section 6).</summary>
/// <param name="InstallId">Stable identifier of the install.</param>
/// <param name="Ring">The ring the install is in for its channel.</param>
/// <param name="Version">The release version string the install currently runs.</param>
public sealed record InstallRingMembership(string InstallId, ReleaseRing Ring, string Version);

/// <summary>What the planner asks for one install.</summary>
public enum RollbackActionKind
{
    /// <summary>Roll the install back to the previous version automatically (canary and early rings).</summary>
    RollBack,

    /// <summary>Do not roll back automatically. The owner or a delegated role decides (broad ring).</summary>
    AwaitingOwnerDecision,
}

/// <summary>One planned action for one install. <see cref="FromVersion"/> is the halted version; <see cref="ToVersion"/> is the previous one.</summary>
public sealed record RollbackAction(
    string InstallId,
    ReleaseRing Ring,
    RollbackActionKind Kind,
    string FromVersion,
    string ToVersion);

/// <summary>Why a rollback plan was refused. Refusal plans no actions.</summary>
public enum RollbackRefusalReason
{
    /// <summary>There is no previous version to roll back to: it is null, or it equals the halted version.</summary>
    NoPreviousVersion,
}

public enum RollbackPlanOutcome
{
    Planned,
    Refused,
}

/// <summary>
/// The result of <see cref="RollbackPlanner.Plan"/>. A planned result carries the actions in ring order, then install id
/// ordinal. A refused result carries a reason and no actions.
/// </summary>
public sealed record RollbackPlan(
    RollbackPlanOutcome Outcome,
    IReadOnlyList<RollbackAction> Actions,
    RollbackRefusalReason? Reason = null);

/// <summary>
/// Plans rollback for a halted release. Pure: no I/O, no clock. The caller supplies the installs and writes the actions.
/// Canary and early installs on the halted version roll back to the previous version. Broad installs are not rolled back
/// automatically and get an <see cref="RollbackActionKind.AwaitingOwnerDecision"/> action instead. Installs already on the
/// previous version, and installs on any other version, get no action.
/// </summary>
public static class RollbackPlanner
{
    public static RollbackPlan Plan(string haltedVersion, string? previousVersion, IEnumerable<InstallRingMembership> installs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(haltedVersion);
        ArgumentNullException.ThrowIfNull(installs);

        if (previousVersion is null || string.Equals(previousVersion, haltedVersion, StringComparison.Ordinal))
        {
            return new RollbackPlan(RollbackPlanOutcome.Refused, [], RollbackRefusalReason.NoPreviousVersion);
        }

        var members = installs.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (!seen.Add(member.InstallId))
            {
                throw new ArgumentException($"Install {member.InstallId} appears more than once.", nameof(installs));
            }
        }

        var actions = members
            .Where(m => string.Equals(m.Version, haltedVersion, StringComparison.Ordinal))
            .OrderBy(m => m.Ring)
            .ThenBy(m => m.InstallId, StringComparer.Ordinal)
            .Select(m => new RollbackAction(
                m.InstallId,
                m.Ring,
                m.Ring == ReleaseRing.Broad ? RollbackActionKind.AwaitingOwnerDecision : RollbackActionKind.RollBack,
                haltedVersion,
                previousVersion))
            .ToList();

        return new RollbackPlan(RollbackPlanOutcome.Planned, actions);
    }
}
