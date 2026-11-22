namespace Bazaar.Server.Gateway.Policy;

/// <summary>
/// Operator settings that can only take access away. Applied on top of every agent policy, they add denials and
/// have no way to allow anything the policy does not. Loosening the floors (deny by default, no wildcards, the
/// credential kinds) is not a setting.
/// </summary>
public sealed class PolicyTightening
{
    public static PolicyTightening None { get; } = new([], []);

    private readonly HashSet<string> _deniedProviders;
    private readonly HashSet<ModelRef> _deniedModels;

    public PolicyTightening(IEnumerable<string> deniedProviders, IEnumerable<ModelRef> deniedModels)
    {
        ArgumentNullException.ThrowIfNull(deniedProviders);
        ArgumentNullException.ThrowIfNull(deniedModels);
        _deniedProviders = new HashSet<string>(deniedProviders, StringComparer.Ordinal);
        _deniedModels = [.. deniedModels];
    }

    public bool Denies(ModelRef model) => _deniedProviders.Contains(model.ProviderId) || _deniedModels.Contains(model);

    /// <summary>Combines two tightenings; the result denies everything either denies.</summary>
    public PolicyTightening And(PolicyTightening other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new PolicyTightening(_deniedProviders.Concat(other._deniedProviders), _deniedModels.Concat(other._deniedModels));
    }
}
