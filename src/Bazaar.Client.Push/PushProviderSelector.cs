using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Push;

/// <summary>
/// Picks the push provider named by <c>android.push_provider</c> (<see cref="AndroidSettings.PushProvider"/>). Each
/// provider is registered once under its <see cref="IPushProvider.Name"/>. A selected kind with no provider is an error,
/// and no fallback is applied, because switching transports is a settings change the operator makes.
/// </summary>
public sealed class PushProviderSelector
{
    private readonly Dictionary<string, IPushProvider> _byName;

    public PushProviderSelector(IEnumerable<IPushProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byName = new Dictionary<string, IPushProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!byName.TryAdd(provider.Name, provider))
            {
                throw new ArgumentException($"More than one push provider is registered as '{provider.Name}'.", nameof(providers));
            }
        }

        _byName = byName;
    }

    /// <summary>The setting value for a kind: <c>unifiedpush</c> or <c>fcm</c>.</summary>
    public static string NameFor(PushProviderKind kind) => kind switch
    {
        PushProviderKind.UnifiedPush => "unifiedpush",
        PushProviderKind.Fcm => "fcm",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown push provider kind."),
    };

    /// <summary>The provider for the selected kind.</summary>
    /// <exception cref="InvalidOperationException">No provider is registered under the kind's name.</exception>
    public IPushProvider Select(PushProviderKind kind)
    {
        var name = NameFor(kind);
        return _byName.TryGetValue(name, out var provider)
            ? provider
            : throw new InvalidOperationException($"android.push_provider is '{name}', but no '{name}' push provider is registered.");
    }
}
