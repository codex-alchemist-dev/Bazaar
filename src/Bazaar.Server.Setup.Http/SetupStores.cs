namespace Bazaar.Server.Setup.Http;

/// <summary>The state of one setup step, as the progress source reports it.</summary>
public enum SetupStepState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>One step and where it stands.</summary>
public sealed record SetupStepProgress(string Id, SetupStepState State);

/// <summary>A change that waits for an approval. Names the key or path, never a value.</summary>
public sealed record SetupHeldApproval(string Path, string Reason);

/// <summary>What <see cref="ISetupProgressSource"/> reports: every step and every held approval.</summary>
public sealed record SetupProgressSnapshot(IReadOnlyList<SetupStepProgress> Steps, IReadOnlyList<SetupHeldApproval> HeldApprovals);

/// <summary>Reports setup progress for <c>GET /setup/status</c>.</summary>
public interface ISetupProgressSource
{
    Task<SetupProgressSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>The Kodo verdict colours (doc 45 section 1.12).</summary>
public enum SetupVerdict
{
    Green,
    Amber,
    Red,
}

/// <summary>The last verification result, when one exists. <see cref="Score"/> is 0 to 100 (doc 45 section 1.12); the source sets it.</summary>
public sealed record SetupVerificationSummary(SetupVerdict Verdict, int Score, DateTimeOffset CheckedAt);

/// <summary>
/// The last verification result. Optional: the verification slice is not in this build, so the status route answers
/// <c>null</c> for the verification when no source is registered.
/// </summary>
public interface ISetupVerificationSource
{
    Task<SetupVerificationSummary?> GetLastAsync(CancellationToken cancellationToken = default);
}
