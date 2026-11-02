namespace Bazaar.Server.Components.Erasure;

/// <summary>
/// One subsystem's erasure step for a person, for data that lives outside the GC log (Vault memory files, the memory store
/// and similar). The GC log itself is erased by key destruction in <see cref="IPersonKeyEraser"/>, not by participants.
/// Participants run after key destruction, in the order they were registered, and must be idempotent: a second run for the
/// same person returns <see cref="ErasureParticipantResult.NothingToDo"/>.
/// </summary>
public interface IErasureParticipant
{
    /// <summary>Stable name shown in the erasure report and in logs.</summary>
    string Name { get; }

    /// <summary>Removes this subsystem's data for the person and reports what was removed.</summary>
    Task<ErasureParticipantResult> EraseAsync(Guid personId, CancellationToken ct = default);
}

public enum ErasureParticipantStatus
{
    /// <summary>The participant removed data; <see cref="ErasureParticipantResult.RemovedCount"/> says how much.</summary>
    Removed,

    /// <summary>The participant had nothing for this person. Counts as success.</summary>
    NothingToDo,

    /// <summary>The participant could not finish. The erasure is incomplete.</summary>
    Failed,
}

/// <summary>What one participant did for one person.</summary>
public sealed record ErasureParticipantResult(ErasureParticipantStatus Status, long RemovedCount = 0, string? Error = null)
{
    public static ErasureParticipantResult Removed(long count) => new(ErasureParticipantStatus.Removed, count);

    public static ErasureParticipantResult NothingToDo() => new(ErasureParticipantStatus.NothingToDo);

    public static ErasureParticipantResult Failed(string error) => new(ErasureParticipantStatus.Failed, Error: error);
}
