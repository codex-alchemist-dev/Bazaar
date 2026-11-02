namespace Bazaar.Server.Memory.Write;

/// <summary>Outcome of committing one memory batch.</summary>
public sealed record MemoryCommitResult
{
    private MemoryCommitResult(bool succeeded, string? commitSha, string? failureReason)
    {
        Succeeded = succeeded;
        CommitSha = commitSha;
        FailureReason = failureReason;
    }

    /// <summary>True when the batch became a commit.</summary>
    public bool Succeeded { get; }

    /// <summary>The new commit sha when <see cref="Succeeded"/> is true.</summary>
    public string? CommitSha { get; }

    /// <summary>Why the commit failed when <see cref="Succeeded"/> is false.</summary>
    public string? FailureReason { get; }

    /// <summary>A successful commit with its sha.</summary>
    public static MemoryCommitResult Success(string commitSha)
    {
        ArgumentException.ThrowIfNullOrEmpty(commitSha);
        return new MemoryCommitResult(true, commitSha, null);
    }

    /// <summary>A failed commit. The writes stay queued and spooled for a later retry.</summary>
    public static MemoryCommitResult Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new MemoryCommitResult(false, null, reason);
    }
}
