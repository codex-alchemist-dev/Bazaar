using Bazaar.Client.Abstractions.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The optional dependencies the heads inject when they build a pane registry. The context names no feature type: a head
/// hands each dependency through <see cref="With{T}"/>, and a module resolves what it needs with <see cref="Get{T}"/>. A
/// module decides what a missing service means for its pane.
/// </summary>
/// <param name="Report">Called once per kind that falls back to the generic view.</param>
public sealed record PaneModuleContext(Action<string>? Report = null)
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
