using Bazaar.Client.Navigation.Abstractions;

namespace Bazaar.Client.Navigation;

/// <summary>
/// A pane surface a head can create before its window and registry exist, then bind once they do. Until it is bound, no
/// pane can be shown, so an early action fails closed instead of reaching a half-built window.
/// </summary>
public sealed class LatePaneSurface : IPaneSurface
{
    private volatile Binding? _binding;

    /// <summary>Binds the surface. A second bind replaces the first (a head that rebuilds its window).</summary>
    public void Bind(Func<string, bool> canShow, Func<string, CancellationToken, Task> show)
    {
        ArgumentNullException.ThrowIfNull(canShow);
        ArgumentNullException.ThrowIfNull(show);

        _binding = new Binding(canShow, show);
    }

    public bool CanShow(string paneKind) => _binding?.CanShow(paneKind) ?? false;

    public Task ShowAsync(string paneKind, CancellationToken cancellationToken) =>
        _binding is { } binding
            ? binding.Show(paneKind, cancellationToken)
            : Task.FromException(new NavigationException("The window is not ready to show panes yet."));

    private sealed record Binding(Func<string, bool> CanShow, Func<string, CancellationToken, Task> Show);
}
