// TODO(component): move to the coding component once PR #125 merges
namespace Bazaar.Server.Coding.Workspace;

/// <summary>A process to run in the guest. Exec request shape from doc 16 (guest protocol).</summary>
/// <param name="Argv">Program and arguments. The first element is the program.</param>
/// <param name="Cwd">Working directory in the guest, absolute.</param>
/// <param name="Env">Environment variables, already filtered to the allowlist.</param>
/// <param name="Timeout">The guest kills the whole process tree when this elapses.</param>
public sealed record ExecRequest(
    IReadOnlyList<string> Argv,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout);

/// <summary>One piece of streamed output from a running process.</summary>
/// <param name="Seq">Position in the process output, starting at 0.</param>
/// <param name="Stream">Either "stdout" or "stderr".</param>
/// <param name="Text">The output text.</param>
public sealed record OutputChunk(long Seq, string Stream, string Text);

/// <summary>How a process ended.</summary>
/// <param name="ExitCode">The process exit code, or -1 when none was reported.</param>
/// <param name="TimedOut">True when the process was stopped for exceeding its timeout.</param>
/// <param name="Cancelled">True when the process was stopped by a cancellation request.</param>
public sealed record ExecResult(int ExitCode, bool TimedOut, bool Cancelled)
{
    /// <summary>True only for a zero exit with no timeout and no cancellation.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>
/// The guest wire as the host sees it (doc 16 IGuestChannel). Provisional: the real channel arrives with doc 16 WP1.
/// Paths are guest paths. Callers must pass jailed paths (see <see cref="GuestPath"/>).
/// </summary>
public interface IGuestChannel
{
    /// <summary>Runs a process. <paramref name="onOutput"/> receives chunks as they arrive.</summary>
    Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct);

    /// <summary>Writes a file. The guest rejects content whose SHA-256 (hex) does not match <paramref name="sha256"/>.</summary>
    Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct);

    /// <summary>Copies a file from the guest into <paramref name="sink"/>.</summary>
    Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct);
}
