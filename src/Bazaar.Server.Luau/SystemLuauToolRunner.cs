using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Production runner backed by <see cref="Process"/>. Arguments go in <see cref="ProcessStartInfo.ArgumentList"/>, never a
/// shell string. Each stream is read up to the output cap and drained past it, so a chatty tool cannot block on a full
/// pipe or grow memory without bound. The whole process tree is killed on timeout or cancellation.
/// </summary>
public sealed class SystemLuauToolRunner : IProcessRunner
{
    private readonly int _maxOutputCharacters;

    /// <param name="maxOutputCharacters">Characters kept per stream. One more is read so the checker can see the output was cut.</param>
    public SystemLuauToolRunner(int maxOutputCharacters = LuauToolOptions.DefaultMaxOutputCharacters)
    {
        if (maxOutputCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters), "The output cap must be positive.");
        }

        _maxOutputCharacters = maxOutputCharacters;
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        // Win32Exception (missing or non-executable file) propagates to the checker, which reports the tool as Unavailable.
        process.Start();
        process.StandardInput.Close();

        // Read both pipes concurrently so a full pipe cannot block the child. The reads end when the process exits or is
        // killed, so they do not take the caller's token; cancellation goes through the wait below.
        Task<string> stdout = ReadCappedAsync(process.StandardOutput, _maxOutputCharacters);
        Task<string> stderr = ReadCappedAsync(process.StandardError, _maxOutputCharacters);

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

    /// <summary>
    /// Reads to end of stream. Keeps one character more than <paramref name="limit"/>, so the checker sees a cut stream
    /// as longer than its cap and marks it truncated; everything past that is drained and discarded.
    /// </summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int limit)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        int keep = limit + 1;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (kept.Length < keep)
            {
                kept.Append(buffer, 0, Math.Min(read, keep - kept.Length));
            }
        }

        return kept.ToString();
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
        catch (Win32Exception)
        {
            // Access denied or already gone; the wait below still completes.
        }
    }
}
