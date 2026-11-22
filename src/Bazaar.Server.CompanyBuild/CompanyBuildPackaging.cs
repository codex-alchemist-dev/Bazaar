using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The installer packages a company build wraps around the client publish (docs/design/37 sections 2 and 4a): the Velopack per-user
/// installer and the MSIX on Windows, the Flatpak bundle on Linux. The packaging scripts are the repository's own
/// (<c>packaging/velopack</c>, <c>packaging/windows-msix</c>, <c>packaging/linux-flatpak</c>); this class only calls them, with an
/// argument list and never through a shell. Each needs tools only its own OS has, so <see cref="Targets"/> lists a package only for the
/// OS the build runs on. The scripts sign through the signer interface, so an unsigned machine produces unsigned packages and the
/// output README labels them.
/// </summary>
public static class CompanyBuildPackaging
{
    public const string VelopackId = "windows-velopack";
    public const string MsixId = "windows-msix";
    public const string FlatpakId = "linux-flatpak";

    /// <summary>The packages this build host can make: Windows packages on Windows, the Flatpak on Linux.</summary>
    public static IReadOnlyList<CompanyBuildTarget> Targets(string sourceRoot, bool isWindows, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var windowsProject = Path.Combine(sourceRoot, "src", "Bazaar.Client.Windows", "Bazaar.Client.Windows.csproj");
        var linuxProject = Path.Combine(sourceRoot, "src", "Bazaar.Client.Linux", "Bazaar.Client.Linux.csproj");
        List<CompanyBuildTarget> targets = [];
        if (isWindows)
        {
            targets.Add(new CompanyBuildTarget(VelopackId, windowsProject, "win-x64"));
            targets.Add(new CompanyBuildTarget(MsixId, windowsProject, "win-x64"));
        }

        if (isLinux)
        {
            targets.Add(new CompanyBuildTarget(FlatpakId, linuxProject, "linux-x64"));
        }

        return targets;
    }

    /// <summary>Arguments for <c>pack-client.ps1</c>. <paramref name="channel"/> is one of stable, beta, nightly or lts; the script refuses anything else.</summary>
    public static IReadOnlyList<string> VelopackArguments(string sourceRoot, string version, string channel, string publishDirectory, string outputDirectory) =>
    [
        "-NoProfile", "-NonInteractive", "-File", Path.Combine(sourceRoot, "packaging", "velopack", "pack-client.ps1"),
        "-Version", Required(version), "-Channel", Required(channel), "-PublishDir", Required(publishDirectory), "-OutputDir", Required(outputDirectory),
    ];

    /// <summary>Arguments for <c>build-msix.ps1</c>. The script publishes the client itself.</summary>
    public static IReadOnlyList<string> MsixArguments(string sourceRoot, string configuration, string outputDirectory) =>
    [
        "-NoProfile", "-NonInteractive", "-File", Path.Combine(sourceRoot, "packaging", "windows-msix", "build-msix.ps1"),
        "-Configuration", Required(configuration), "-OutDir", Required(outputDirectory),
    ];

    /// <summary>Arguments for <c>build-flatpak.sh</c> (run with <c>bash</c>). The output directory is its positional argument.</summary>
    public static IReadOnlyList<string> FlatpakArguments(string sourceRoot, string outputDirectory) =>
    [
        Path.Combine(sourceRoot, "packaging", "linux-flatpak", "build-flatpak.sh"), Required(outputDirectory),
    ];

    /// <summary>
    /// The build step for <see cref="CompanyBuildPipeline"/>: packaging targets run their script, every other target publishes as
    /// <see cref="CompanyBuildTargets.PublishStep"/> does. The Velopack target publishes into a scratch folder first and removes it after.
    /// </summary>
    public static Func<CompanyBuildTarget, string, CancellationToken, Task> Step(string sourceRoot, string version, string channel, string configuration = "Release")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var publish = CompanyBuildTargets.PublishStep(configuration);
        return async (target, outputDirectory, cancellationToken) =>
        {
            switch (target.Id)
            {
                case VelopackId:
                    var scratch = Path.Combine(Path.GetTempPath(), "bazaar-company-build-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await publish(target, scratch, cancellationToken).ConfigureAwait(false);
                        await ProcessRunner.RunAsync("pwsh", VelopackArguments(sourceRoot, version, channel, scratch, outputDirectory), sourceRoot, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (Directory.Exists(scratch))
                        {
                            Directory.Delete(scratch, recursive: true);
                        }
                    }

                    break;
                case MsixId:
                    await ProcessRunner.RunAsync("pwsh", MsixArguments(sourceRoot, configuration, outputDirectory), sourceRoot, cancellationToken).ConfigureAwait(false);
                    break;
                case FlatpakId:
                    await ProcessRunner.RunAsync("bash", FlatpakArguments(sourceRoot, outputDirectory), sourceRoot, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await publish(target, outputDirectory, cancellationToken).ConfigureAwait(false);
                    break;
            }
        };
    }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
