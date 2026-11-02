using System.Diagnostics;

namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>Captured outcome of one child process.</summary>
/// <param name="ExitCode">Exit code. Meaningless when <paramref name="TimedOut"/> is true.</param>
/// <param name="StandardOutput">Everything written to stdout (possibly partial after a timeout).</param>
/// <param name="StandardError">Everything written to stderr (possibly partial after a timeout).</param>
/// <param name="TimedOut">True when the process was killed for exceeding its timeout.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs a child process from an argument list (never a shell string). Injected so tests need no real binary.
/// Throws <see cref="Win32Exception"/> when the file cannot be started, and <see cref="OperationCanceledException"/>
/// when the caller's token fires.
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

/// <summary>Production runner backed by <see cref="Process"/>. Kills the whole process tree on timeout or cancel.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain both pipes concurrently so a full pipe cannot block the child. The pipes end when the process
        // exits or is killed, so the reads do not need the caller's token; cancellation goes through the wait below.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            return new ProcessResult(process.ExitCode, await stdout, await stderr, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr, TimedOut: false);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or already gone; the wait below still completes.
        }
    }
}
