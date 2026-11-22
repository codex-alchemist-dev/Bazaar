using Bazaar.Server.Portability.Sandbox;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.WindowsSandbox;
using Bazaar.Server.Vms.WindowsSandbox.WsbCli;

namespace Bazaar.Server.Providers.Windows;

/// <summary>The Windows provider candidates for the composition root. Empty off Windows, so no Windows type is built there.</summary>
public static class WindowsProviders
{
    /// <summary>The service host candidates in preference order. Returns an empty list when the host is not Windows.</summary>
    public static IReadOnlyList<IServiceHostProvider> ServiceHostCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return [new WindowsServiceHostProvider()];
    }

    /// <summary>
    /// The VM provider candidates in preference order. Hyper-V goes first once it exists; the sandbox is listed after it.
    /// Returns an empty list when the host is not Windows. The sandbox declares one instance per host, so the agent pair
    /// (which needs two) must not use it for the pair (the consuming module checks the declared limit).
    /// </summary>
    public static IReadOnlyList<IVmProvider> VmCandidates(string dataRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var configDirectory = Path.Combine(dataRoot, "vms", "windows-sandbox");
        // No base image is configured yet, so the empty id is the only parent id it could match. Any real parent is refused,
        // which keeps child creation fail closed until an operator sets the image.
        var sandbox = new WindowsSandboxBackend(
            new WsbCliSandboxHost(new ProcessWsbCommandRunner(), configDirectory),
            new WindowsSandboxHostFacts(),
            new WindowsSandboxOptions(Guid.Empty));
        return [sandbox];
    }

    /// <summary>
    /// The job-object confinement for the portability parser child (doc 44 section 8.3), or null when the host is not Windows
    /// (the runner then uses its own OS default).
    /// </summary>
    public static IParserConfinement? ParserConfinement() =>
        OperatingSystem.IsWindows() ? new WindowsJobObjectConfinement() : null;
}
