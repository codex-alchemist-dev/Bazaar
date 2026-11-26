using System.Diagnostics;
using System.Text;
using Bazaar.Engines.Processes;

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
    /// <summary>Most characters kept from each of stdout and stderr. The rest of a flood is read and dropped.</summary>
    public const int MaxCapturedChars = 4 * 1024 * 1024;

    private static readonly TimeSpan _pipeDrainWait = TimeSpan.FromSeconds(5);

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

        // The child gets an allowlisted environment, never the server's own secrets.
        ChildProcessEnvironment.Apply(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain both pipes concurrently so a full pipe cannot block the child. The pipes end when the process
        // exits or is killed, so the reads do not need the caller's token; cancellation goes through the wait below.
        var stdout = new CappedCapture(process.StandardOutput, MaxCapturedChars);
        var stderr = new CappedCapture(process.StandardError, MaxCapturedChars);

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
            return new ProcessResult(process.ExitCode, await stdout.FinishAsync(_pipeDrainWait), await stderr.FinishAsync(_pipeDrainWait), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.FinishAsync(_pipeDrainWait), await stderr.FinishAsync(_pipeDrainWait), TimedOut: false);
    }

    /// <summary>
    /// Drains one pipe, keeping at most a cap of characters. Reading continues past the cap so the child never blocks on
    /// a full pipe. A grandchild that outlives the child and holds the pipe open cannot stall the caller: the wait for the
    /// end of the stream is bounded and the text read so far is returned.
    /// </summary>
    private sealed class CappedCapture
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private readonly Task _pump;

        public CappedCapture(TextReader reader, int cap)
        {
            _pump = Task.Run(async () =>
            {
                var buffer = new char[8192];
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        int room = cap - _text.Length;
                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(read, room));
                        }
                    }
                }
            });
        }

        public async Task<string> FinishAsync(TimeSpan wait)
        {
            try
            {
                await _pump.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Something still holds the pipe open; return what was read.
            }
            catch (IOException)
            {
                // The pipe broke; return what was read.
            }

            lock (_gate)
            {
                return _text.ToString();
            }
        }
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
