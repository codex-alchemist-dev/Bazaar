namespace Bazaar.Kodo.SelfChecks;

/// <summary>What a runner is told about the run.</summary>
/// <param name="DefinitionId">The definition being run.</param>
/// <param name="Kind">Which check.</param>
/// <param name="Slot">The due instant this run answers.</param>
/// <param name="StartedAt">When the run started.</param>
public sealed record SelfCheckContext(string DefinitionId, SelfCheckKind Kind, DateTimeOffset Slot, DateTimeOffset StartedAt);

/// <summary>
/// Runs one kind of self-check (doc 46 section 17.2). Interface only: the concrete runners (hash sweep, restore into a scratch
/// database, recovery bundle rehearsal, failover drill) need doc 17 sandboxes and doc 33 restore, and are not built. A runner works
/// only on sandbox or scratch copies, never on production keys, and honours the token it is given.
/// </summary>
public interface ISelfCheckRunner
{
    /// <summary>The kind this runner handles. One runner per kind.</summary>
    SelfCheckKind Kind { get; }

    /// <summary>Runs the check. A throw is contained by the scheduler and recorded as faulted.</summary>
    Task<SelfCheckRunnerResult> RunAsync(SelfCheckContext context, CancellationToken cancellationToken);
}
