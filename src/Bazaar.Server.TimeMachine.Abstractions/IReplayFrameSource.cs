using Bazaar.Contracts.Gc;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Seams for the parts of a frame other modules own. A module that is not installed registers nothing, and the projector reports
/// that part as unavailable. Implementations apply the viewer's rights themselves.
/// </summary>
/// <typeparam name="T">The part's value type.</typeparam>
public interface IReplayFrameSource<T>
{
    /// <summary>The state of this part for the GC at <paramref name="seq"/>.</summary>
    Task<T> GetAsync(Guid gcId, long seq, GcPrincipal viewer, CancellationToken ct);
}

/// <summary>Kanban state rebuilt from the task event log (docs/design/12).</summary>
public interface IKanbanReplaySource : IReplayFrameSource<IReadOnlyList<ReplayTask>>;

/// <summary>The nearest Vault commit (docs/design/18).</summary>
public interface IVaultReplaySource : IReplayFrameSource<ReplayVaultState>;

/// <summary>Soul versions in effect (docs/design/07).</summary>
public interface ISoulReplaySource : IReplayFrameSource<IReadOnlyList<ReplaySoulVersion>>;

/// <summary>Reputation in effect (docs/design/11).</summary>
public interface IReputationReplaySource : IReplayFrameSource<IReadOnlyList<ReplayReputation>>;
