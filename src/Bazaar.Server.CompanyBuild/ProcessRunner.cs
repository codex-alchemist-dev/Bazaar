using System.Diagnostics;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// Runs one external build program with an argument list, never through a shell, so a company id or path cannot inject commands.
/// A non-zero exit throws with the tail of stderr, which the runner reports and retries.
/// </summary>
public static class ProcessRunner
{
    /// <exception cref="InvalidOperationException">The process could not start or exited with a non-zero code.</exception>
    public static async Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{fileName} could not be started.");
        using var stop = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        var errorText = (await stderr.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var tail = errorText.Length <= 500 ? errorText : errorText[^500..];
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}: {tail}");
        }
    }
}
