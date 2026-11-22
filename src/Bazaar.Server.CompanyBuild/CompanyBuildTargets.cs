using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The client heads a company build publishes (docs/design/37 section 2 and 4a). Packaging formats (MSIX, Velopack, Flatpak, APK)
/// wrap these publish outputs later; their spikes are open in doc 37, so this slice produces only the publish folders.
/// </summary>
public static class CompanyBuildTargets
{
    /// <summary>The desktop heads, self-contained. Android needs the Android workload and its own packaging step, so it is added with the APK slice.</summary>
    public static IReadOnlyList<CompanyBuildTarget> Desktop(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        return
        [
            new CompanyBuildTarget(
                "windows-x64",
                Path.Combine(sourceRoot, "src", "Bazaar.Client.Windows", "Bazaar.Client.Windows.csproj"),
                "win-x64"),
            new CompanyBuildTarget(
                "linux-x64",
                Path.Combine(sourceRoot, "src", "Bazaar.Client.Linux", "Bazaar.Client.Linux.csproj"),
                "linux-x64"),
        ];
    }

    /// <summary>The build step for <see cref="CompanyBuildPipeline"/>: publishes one target into the folder the pipeline gives it.</summary>
    public static Func<CompanyBuildTarget, string, CancellationToken, Task> PublishStep(string configuration = "Release") =>
        (target, outputDirectory, cancellationToken) => DotnetPublishBuildStep.RunAsync(
            target.ProjectPath,
            outputDirectory,
            cancellationToken,
            configuration,
            target.RuntimeIdentifier);
}
