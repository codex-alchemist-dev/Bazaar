namespace Bazaar.Server.Backup.Processes;

/// <summary>Captured result of one external process. Output is for logs and error text only.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs an external tool with an argument list (never a shell string). Environment values may name files, such as a
/// restic password file, but must not carry secret values in arguments.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct);
}

/// <summary>A tool exited non-zero. The message names the tool and exit code, never its arguments.</summary>
public sealed class ProcessFailedException(string tool, int exitCode, string stdErr)
    : Exception($"{tool} exited with code {exitCode}: {Trim(stdErr)}")
{
    private static string Trim(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim();
}
