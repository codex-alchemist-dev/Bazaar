using System.Diagnostics;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The publish step a company build runs (docs/design/37 section 4a): <c>dotnet publish</c> of one client project into an output
/// folder. The arguments are passed as a list, never through a shell, so a company id or path cannot inject commands.
/// </summary>
public static class DotnetPublishBuildStep
{
    /// <summary>The arguments for one publish, in order.</summary>
    public static IReadOnlyList<string> BuildArguments(string projectPath, string outputDirectory, string configuration = "Release", string? runtimeIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        List<string> arguments = ["publish", projectPath, "-c", configuration, "-o", outputDirectory, "--nologo"];
        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            // A RID makes the desktop heads self-contained and turns on their ReadyToRun precompilation (doc 40).
            arguments.AddRange(["-r", runtimeIdentifier, "--self-contained", "true"]);
        }

        return arguments;
    }

    /// <summary>Runs the publish and throws with the tail of stderr when it exits non-zero, so the runner can report it.</summary>
    /// <exception cref="InvalidOperationException">The process could not start or exited with a non-zero code.</exception>
    public static async Task RunAsync(
        string projectPath,
        string outputDirectory,
        CancellationToken cancellationToken,
        string configuration = "Release",
        string? runtimeIdentifier = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in BuildArguments(projectPath, outputDirectory, configuration, runtimeIdentifier))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The dotnet command could not be started.");
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
            throw new InvalidOperationException($"dotnet publish exited with code {process.ExitCode}: {tail}");
        }
    }
}
