using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// Production runner backed by <see cref="Process"/>. Arguments go in <see cref="ProcessStartInfo.ArgumentList"/>, never a shell
/// string. Each stream is read up to the output cap and drained past it, so a chatty engine cannot block on a full pipe or grow
/// memory without bound. The whole process tree is killed on timeout or cancellation. Registered only when the host has not
/// registered its own <see cref="IProcessRunner"/>: this module may not reference the Engines implementation project.
/// </summary>
public sealed class SystemGodotProcessRunner : IProcessRunner
{
    private readonly int _maxOutputCharacters;

    /// <param name="maxOutputCharacters">Characters kept per stream. One more is read so the adapter can see the output was cut.</param>
    public SystemGodotProcessRunner(int maxOutputCharacters = GodotAdapterOptions.DefaultMaxOutputCharacters)
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
        // Win32Exception (missing or non-executable file) propagates to the adapter, which reports Unavailable.
        process.Start();
        process.StandardInput.Close();

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
            // Access denied or already gone; the wait still completes.
        }
    }
}
