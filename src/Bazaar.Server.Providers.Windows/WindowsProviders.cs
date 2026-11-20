using Bazaar.Server.Providers;

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

    // VM providers are not listed here: each provider module contributes an IVmBackendSource through DI, and the host orders the
    // pair candidates (doc 62 section 9.2).
}
