using Bazaar.Server.Secrets;

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

    /// <summary>Cache candidates: Valkey. Empty off Linux, so the Windows host never sees it (Garnet waits for spike S2).</summary>
    public static IReadOnlyList<ICacheProvider> CacheCandidates(ValkeyCacheOptions options, ISecretResolver? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        return [new ValkeyCacheProvider(options, secretResolver, new StackExchangeValkeyClientFactory())];
    }
}
