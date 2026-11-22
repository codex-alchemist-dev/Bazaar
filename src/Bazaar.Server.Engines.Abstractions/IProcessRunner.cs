using System.ComponentModel;

namespace Bazaar.Server.Engines.Abstractions;

/// <summary>Captured outcome of one child process.</summary>
/// <param name="ExitCode">Exit code. Meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="StandardOutput">Everything written to stdout (possibly partial after a timeout, or cut at the runner's output limit).</param>
/// <param name="StandardError">Everything written to stderr (possibly partial after a timeout, or cut at the runner's output limit).</param>
/// <param name="TimedOut">True when the process was killed for exceeding its timeout.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs a child process from an argument list (never a shell string). The one process seam shared by the Rojo runner and the
/// Luau checks (docs/design/40, section 4b), injected so tests need no real binary. Throws <see cref="Win32Exception"/> or
/// <see cref="FileNotFoundException"/> when the file cannot be started, and <see cref="OperationCanceledException"/> when the
/// caller's token fires.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
