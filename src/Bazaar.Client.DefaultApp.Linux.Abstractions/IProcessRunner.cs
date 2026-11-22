namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>A child process to run. Arguments are passed as a list, never through a shell.</summary>
/// <param name="FileName">Executable name or path.</param>
/// <param name="Arguments">Arguments, one element each.</param>
/// <param name="Environment">Variables added to the child's environment.</param>
/// <param name="Timeout">The run is killed and reported as <see cref="TimeoutException"/> after this long.</param>
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

/// <summary>What a finished process produced.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs <c>xdg-mime</c> and <c>update-desktop-database</c>. A seam so tests do not start a process.</summary>
public interface IProcessRunner
{
    /// <summary>Runs the process to completion.</summary>
    /// <exception cref="TimeoutException">The process outlived <see cref="ProcessRequest.Timeout"/> and was killed.</exception>
    /// <exception cref="IOException">The executable could not be started.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct);
}
