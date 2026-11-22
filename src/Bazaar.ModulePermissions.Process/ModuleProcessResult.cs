namespace Bazaar.ModulePermissions.Process;

/// <summary>What one run of a program gave back.</summary>
/// <param name="ExitCode">The program's exit code. Meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="StandardOutput">Standard output, up to the limit.</param>
/// <param name="StandardError">Standard error, up to the limit.</param>
/// <param name="TimedOut">True when the run reached its time limit and was killed.</param>
/// <param name="OutputTruncated">True when either stream was longer than the limit, or was still open when the wait for it ended.</param>
public sealed record ModuleProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputTruncated);
