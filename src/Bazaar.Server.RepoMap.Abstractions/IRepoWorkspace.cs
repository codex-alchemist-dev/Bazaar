namespace Bazaar.Server.RepoMap;

/// <summary>
/// Port to the worktree the repo map and search tool read from. It runs argv only (never a shell), with the worktree root as the working directory.
/// The implementation enforces the path jail and kills the process tree when the timeout elapses.
/// The coding harness supplies this over its workspace facade.
/// </summary>
public interface IRepoWorkspace
{
    /// <summary>
    /// Runs <paramref name="argv"/> at the worktree root and waits for it to end. Output beyond <paramref name="maxOutputBytes"/> is cut and flagged.
    /// </summary>
    Task<WorkspaceOutput> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, int maxOutputBytes, CancellationToken ct);

    /// <summary>
    /// Reads a worktree-relative file. Returns null when the file is missing or larger than <paramref name="maxBytes"/>.
    /// </summary>
    Task<byte[]?> ReadFileAsync(string relativePath, int maxBytes, CancellationToken ct);
}

/// <summary>The outcome of one <see cref="IRepoWorkspace.RunAsync"/> call.</summary>
/// <param name="ExitCode">The process exit code. Meaningless when <see cref="TimedOut"/> is true.</param>
/// <param name="TimedOut">True when the timeout elapsed. A timeout is never a success.</param>
/// <param name="Stdout">Standard output, cut at the output cap. Untrusted data.</param>
/// <param name="OutputTruncated">True when the output was cut at the cap, so the last item may be incomplete.</param>
public sealed record WorkspaceOutput(int ExitCode, bool TimedOut, string Stdout, bool OutputTruncated);

/// <summary>
/// Raised when a repo tool cannot give a complete answer: a timeout, a failed process, or a failed listing.
/// </summary>
public sealed class RepoToolException(string message) : Exception(message);
