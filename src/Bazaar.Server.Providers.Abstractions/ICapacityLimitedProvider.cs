namespace Bazaar.Server.Providers;

/// <summary>
/// A provider that can run only so many instances at once on one host. A consuming module compares the declared limit with the
/// number it needs and decides what to do with a provider whose limit is lower (fail closed, doc 50 section 6). Core selects nothing.
/// Providers without a limit are not checked, so they stay unlimited.
/// </summary>
public interface ICapacityLimitedProvider : IProvider
{
    /// <summary>The most instances this provider can run at the same time on this host. Must be 1 or more.</summary>
    int MaxConcurrentInstances { get; }
}

/// <summary>Concurrency that a consumer needs from a provider.</summary>
public static class ProviderRequirements
{
    /// <summary>
    /// The agent pair (doc 16, C03): one Windows VM and one Linux VM running at once for each agent. A VM provider that cannot run
    /// two instances at once cannot serve the pair, so the consuming module does not use it for the pair.
    /// </summary>
    public const int AgentPairConcurrency = 2;
}
