namespace Bazaar.Server.Providers;

/// <summary>
/// A provider that can run only so many instances at once on one host. The selector compares the declared limit with the number
/// a role needs, and refuses a provider whose limit is lower (fail closed, doc 50 section 6). Providers without a limit are not
/// checked, so they stay unlimited.
/// </summary>
public interface ICapacityLimitedProvider : IProvider
{
    /// <summary>The most instances this provider can run at the same time on this host. Must be 1 or more.</summary>
    int MaxConcurrentInstances { get; }
}

/// <summary>Concurrency that a role needs from its provider.</summary>
public static class ProviderRequirements
{
    /// <summary>
    /// The agent pair (doc 16, C03): one Windows VM and one Linux VM running at once for each agent. A VM provider that cannot run
    /// two instances at once cannot serve the pair, so it is not eligible for the VM role.
    /// </summary>
    public const int AgentPairConcurrency = 2;
}
