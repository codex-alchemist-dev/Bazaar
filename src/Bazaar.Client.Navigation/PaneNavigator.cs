using Bazaar.Client.Navigation.Abstractions;

namespace Bazaar.Client.Navigation;

/// <summary>
/// The pane navigator. A target with a scheme goes to the handler registered for that scheme (a module adds <c>gc</c>,
/// <c>vault</c> and so on); a target with none is a pane kind. An unknown scheme fails closed.
/// </summary>
public sealed class PaneNavigator : IPaneNavigator
{
    private readonly IPaneSurface _surface;
    private readonly Dictionary<string, Func<string, CancellationToken, Task>> _schemes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PaneNavigator(IPaneSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        _surface = surface;
    }

    /// <summary>Adds a scheme. A second registration of the same scheme throws, so two modules cannot both claim it.</summary>
    public void RegisterScheme(string scheme, Func<string, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_schemes.TryAdd(scheme, handler))
            {
                throw new InvalidOperationException($"Navigation scheme '{scheme}' is already registered.");
            }
        }
    }

    public Task OpenPaneAsync(string paneKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneKind);

        return _surface.CanShow(paneKind)
            ? _surface.ShowAsync(paneKind, cancellationToken)
            : Task.FromException(new NavigationException($"No module provides the pane '{paneKind}'."));
    }

    public Task NavigateAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        int colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return OpenPaneAsync(target, cancellationToken);
        }

        string scheme = target[..colon];
        string value = target[(colon + 1)..];
        Func<string, CancellationToken, Task>? handler;
        lock (_gate)
        {
            _schemes.TryGetValue(scheme, out handler);
        }

        return handler is null
            ? Task.FromException(new NavigationException($"No module handles the navigation scheme '{scheme}'."))
            : handler(value, cancellationToken);
    }
}
