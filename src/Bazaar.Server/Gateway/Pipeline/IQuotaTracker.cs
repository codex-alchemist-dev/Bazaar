using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Health;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>
/// The quota side of the pipeline (doc 05, work package 8). Quota is per provider key and model class, never per agent.
/// </summary>
public interface IQuotaTracker
{
    /// <summary>The state that blocks the key for the model class now, or null when calls may go ahead.</summary>
    QuotaState? BlockedState(ProviderKey key, string modelClass);

    /// <summary>When the block ends, if known. Used for <c>Retry-After</c> and capacity events.</summary>
    DateTimeOffset? BlockedUntil(ProviderKey key, string modelClass) => null;

    /// <summary>Tells the tracker about a failed attempt on the key.</summary>
    void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure);
}

/// <summary>Tracks nothing and blocks nothing. The default until the real tracker exists.</summary>
public sealed class NullQuotaTracker : IQuotaTracker
{
    public static NullQuotaTracker Instance { get; } = new();

    public QuotaState? BlockedState(ProviderKey key, string modelClass) => null;

    public void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure)
    {
    }
}
