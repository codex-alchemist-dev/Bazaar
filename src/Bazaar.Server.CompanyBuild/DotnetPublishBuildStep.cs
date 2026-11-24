namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The publish step a company build runs (docs/design/37 section 4a): <c>dotnet publish</c> of one client project into an output
/// folder. The arguments are passed as a list, never through a shell, so a company id or path cannot inject commands.
/// </summary>
public static class DotnetPublishBuildStep
{
    /// <summary>The arguments for one publish, in order.</summary>
    public static IReadOnlyList<string> BuildArguments(string projectPath, string outputDirectory, string configuration = "Release", string? runtimeIdentifier = null, IReadOnlyList<string>? extraArguments = null)
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

        if (extraArguments is not null)
        {
            arguments.AddRange(extraArguments);
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
        string? runtimeIdentifier = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        await ProcessRunner.RunAsync(
            "dotnet",
            BuildArguments(projectPath, outputDirectory, configuration, runtimeIdentifier, extraArguments),
            workingDirectory: null,
            cancellationToken).ConfigureAwait(false);
    }
}
