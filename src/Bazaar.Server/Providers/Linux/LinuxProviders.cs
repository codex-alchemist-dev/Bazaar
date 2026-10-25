namespace Bazaar.Server.Providers.Linux;

/// <summary>The Linux provider candidates, in preference order. Empty on every other OS, so no Linux module is registered there.</summary>
public static class LinuxProviders
{
    /// <summary>Service host candidates: systemd. Callers still check <see cref="ProviderSelector"/> results at startup.</summary>
    public static IReadOnlyList<IServiceHostProvider> ServiceHostCandidates()
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        return [new SystemdServiceHostProvider()];
    }
}
