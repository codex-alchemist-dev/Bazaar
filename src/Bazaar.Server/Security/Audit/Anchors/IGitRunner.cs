namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>Result of one git invocation: the exit code and the standard output.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput);

/// <summary>
/// Runs the git CLI for <see cref="OffsiteGitTarget"/>. The arguments are passed as a list and never through a shell.
/// Tests use a fake runner; <see cref="ProcessGitRunner"/> is the real one.
/// </summary>
public interface IGitRunner
{
    /// <summary>Runs git with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct);
}
