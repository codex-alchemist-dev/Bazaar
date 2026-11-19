namespace Bazaar.Server.TalentPool;

/// <summary>Result carried by a <c>council.decision</c> for a <c>hire</c> motion (docs/design/10, C32; docs/design/25, Council hire hook).</summary>
public enum CouncilHireResult
{
    /// <summary>Unanimous: passes without the owner.</summary>
    Passed,
    Failed,
    /// <summary>Split or tied on a bigger change. Not final: the owner's call follows.</summary>
    EscalatedToOwner,
    OwnerApproved,
    OwnerRejected,
}

/// <summary>A <c>council.decision</c> outcome as the Talent Pool consumes it.</summary>
public sealed record CouncilHireOutcome(Guid MotionId, Guid TemplateId, int Version, CouncilHireResult Result);

/// <summary>What the Talent Pool asks the Council to vote on. The content hash pins the vote to exactly this content.</summary>
public sealed record HireMotionRequest(Guid TemplateId, int Version, string ContentSha, string ProposedBy);

/// <summary>Port for opening a <c>hire</c> motion. The Council module adapts it; the Council sets the motion class, not the Talent Pool.</summary>
public interface ICouncilHireMotions
{
    /// <summary>Opens the motion and returns its id.</summary>
    Task<Guid> SubmitHireMotionAsync(HireMotionRequest request, CancellationToken ct);
}

/// <summary>A submitted motion and its recorded outcome (null while pending).</summary>
public sealed record HireMotionRecord(Guid MotionId, Guid TemplateId, int Version, string ContentSha, CouncilHireResult? Result);

/// <summary>Durable record of hire motions and their outcomes. The Postgres store lands with the Council adapter.</summary>
public interface ITalentHireOutcomeStore
{
    /// <summary>Records a submitted motion. Returns false when the motion id is already recorded.</summary>
    Task<bool> AddMotionAsync(HireMotionRecord motion, CancellationToken ct);

    Task<HireMotionRecord?> GetMotionAsync(Guid motionId, CancellationToken ct);

    /// <summary>All motions recorded for one template version.</summary>
    Task<IReadOnlyList<HireMotionRecord>> ListMotionsAsync(Guid templateId, int version, CancellationToken ct);

    /// <summary>Stores the result on a known motion. The caller decides whether the transition is allowed.</summary>
    Task SetResultAsync(Guid motionId, CouncilHireResult result, CancellationToken ct);
}

/// <summary>An audited Talent Pool event (kind such as <c>talent.hire_outcome_ignored</c>).</summary>
public sealed record TalentAuditEvent(string Kind, Guid? MotionId, Guid? TemplateId, int? Version, string Detail);

/// <summary>Port for the audit trail; the audit module adapts it.</summary>
public interface ITalentAuditSink
{
    Task RecordAsync(TalentAuditEvent auditEvent, CancellationToken ct);
}

/// <summary>Default sink that drops events, until an audit adapter is registered.</summary>
public sealed class NullTalentAuditSink : ITalentAuditSink
{
    public Task RecordAsync(TalentAuditEvent auditEvent, CancellationToken ct) => Task.CompletedTask;
}
