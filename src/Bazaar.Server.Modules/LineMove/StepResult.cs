namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// What a step executor reports. A success carries evidence (and, for the snapshot step, the snapshot id);
/// a failure carries the reason. Cancellation is not a result: executors throw <see cref="OperationCanceledException"/>.
/// </summary>
public sealed record StepResult
{
    private StepResult(bool succeeded, string text, string? snapshotId)
    {
        Succeeded = succeeded;
        Text = text;
        SnapshotId = snapshotId;
    }

    /// <summary>True when the step passed.</summary>
    public bool Succeeded { get; }

    /// <summary>Evidence on success, the failure reason otherwise.</summary>
    public string Text { get; }

    /// <summary>Snapshot id reported by the snapshot step; null for every other step.</summary>
    public string? SnapshotId { get; }

    /// <summary>A passed step with its evidence string.</summary>
    public static StepResult Success(string evidence, string? snapshotId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        return new StepResult(true, evidence, snapshotId);
    }

    /// <summary>A failed step with its reason.</summary>
    public static StepResult Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new StepResult(false, reason, null);
    }
}
