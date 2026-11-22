namespace Bazaar.Server.Luau.Abstractions;

/// <summary>Captured outcome of one tool process.</summary>
/// <param name="ExitCode">Exit code. Meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="StandardOutput">Everything written to stdout, possibly cut at the runner's output limit.</param>
/// <param name="StandardError">Everything written to stderr, possibly cut at the runner's output limit.</param>
/// <param name="TimedOut">True when the process was killed for exceeding its timeout.</param>
public sealed record LuauProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs a tool from an argument list (never a shell string). Same seam as the Rojo runner in Bazaar.Engines, so tests
/// need no real binary. Throws <see cref="System.ComponentModel.Win32Exception"/> or <see cref="FileNotFoundException"/>
/// when the file cannot be started, and <see cref="OperationCanceledException"/> when the caller's token fires.
/// </summary>
public interface ILuauToolRunner
{
    Task<LuauProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
