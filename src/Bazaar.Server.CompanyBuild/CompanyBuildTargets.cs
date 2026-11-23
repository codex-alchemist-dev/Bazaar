using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The client heads a company build publishes (docs/design/37 section 2 and 4a). Packaging formats (MSIX, Velopack, Flatpak, APK)
/// wrap these publish outputs later; their spikes are open in doc 37, so this slice produces only the publish folders.
/// </summary>
public static class CompanyBuildTargets
{
    /// <summary>The desktop heads, self-contained. Android is <see cref="Android"/>: it needs the Android workload on the build machine.</summary>
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

    /// <summary>
    /// The Android head as an APK (doc 37 section 2). It publishes with <c>BazaarAndroid=true</c> on the Android framework, as
    /// <c>packaging/android/build-android.sh</c> does, and asks for the APK only: the AAB is for Play and is built by the release
    /// workflow. The build is unsigned unless the build machine's environment signs it, and the output folder's README labels
    /// unsigned sets. The machine needs the Android workload and SDK, so a company build lists this target only when it has them.
    /// </summary>
    public static CompanyBuildTarget Android(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        return new CompanyBuildTarget(
            "android-apk",
            Path.Combine(sourceRoot, "src", "Bazaar.Client.Android", "Bazaar.Client.Android.csproj"),
            ExtraArguments: ["-f", "net11.0-android", "-p:BazaarAndroid=true", "-p:AndroidPackageFormats=apk"]);
    }

    /// <summary>The build step for <see cref="CompanyBuildPipeline"/>: publishes one target into the folder the pipeline gives it.</summary>
    public static Func<CompanyBuildTarget, string, CancellationToken, Task> PublishStep(string configuration = "Release") =>
        (target, outputDirectory, cancellationToken) => DotnetPublishBuildStep.RunAsync(
            target.ProjectPath,
            outputDirectory,
            cancellationToken,
            configuration,
            target.RuntimeIdentifier,
            target.ExtraArguments);
}
