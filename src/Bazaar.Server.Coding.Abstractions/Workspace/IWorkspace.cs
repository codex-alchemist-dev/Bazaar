namespace Bazaar.Server.Coding.Workspace;

/// <summary>
/// Runs processes inside one worktree and streams their output. The coding harness talks to the guest only through this facade.
/// </summary>
public interface IWorkspace : IAsyncDisposable
{
    /// <summary>
    /// Starts a process. <paramref name="ct"/> cancels the process while it runs, so pass a token that lives as long as the process should.
    /// </summary>
    Task<ProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct);

    /// <summary>Replays buffered output from sequence <paramref name="fromSeq"/>, then follows it until the process ends.</summary>
    IAsyncEnumerable<OutputChunk> StreamAsync(ProcessHandle h, long fromSeq, CancellationToken ct);

    /// <summary>
    /// Starts a process and waits for it to end. Output is buffered as in <see cref="StartAsync"/>.
    /// A timeout reports <see cref="ExecResult.TimedOut"/> and never success.
    /// </summary>
    Task<ExecResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>A process to run inside the worktree.</summary>
/// <param name="Argv">Program and arguments. The first element is the program; must be non-empty.</param>
/// <param name="Cwd">Working directory relative to the worktree root. Use "." for the root.</param>
/// <param name="Env">Environment variables for the process.</param>
/// <param name="Timeout">Positive. The whole process tree is killed when it elapses.</param>
public sealed record ProcessSpec(
    IReadOnlyList<string> Argv,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout);

/// <summary>Identifies a started process within one workspace.</summary>
public sealed record ProcessHandle(Guid Id);
