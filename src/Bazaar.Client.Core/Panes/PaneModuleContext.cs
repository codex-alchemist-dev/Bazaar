using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Vault;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The optional dependencies the heads inject when they build a pane registry. Every member may be null; a module
/// decides what a null means for its pane. A dependency with no member here travels through <see cref="With{T}"/>.
/// </summary>
/// <param name="Tiles">The Constellation tile source. Null leaves the pane without tile requests.</param>
/// <param name="Vault">The Vault API. Null builds one for the current client server.</param>
/// <param name="Chat">The group chat the GC pane shows. Null shows the pane's empty state.</param>
/// <param name="Report">Called once per kind that falls back to the generic view.</param>
public sealed record PaneModuleContext(
    IConstellationTileSource? Tiles = null,
    VaultApi? Vault = null,
    ChatViewModel? Chat = null,
    Action<string>? Report = null)
{
    private Dictionary<Type, object> Services { get; init; } = new Dictionary<Type, object>();

    /// <summary>
    /// Returns a copy of this context that also carries <paramref name="service"/>, keyed by <typeparamref name="T"/>.
    /// A head uses it to hand a feature module a dependency that Client.Core has no type for.
    /// </summary>
    public PaneModuleContext With<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return this with { Services = new Dictionary<Type, object>(Services) { [typeof(T)] = service } };
    }

    /// <summary>The service a head added with <see cref="With{T}"/>, or null when it added none.</summary>
    public T? Get<T>()
        where T : class =>
        Services.TryGetValue(typeof(T), out var service) ? (T)service : null;
}
