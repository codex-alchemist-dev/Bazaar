namespace Bazaar.Client.Panes.Abstractions;

/// <summary>
/// A navigator a head creates before its layout exists, because the pane registry needs the navigator and the layout needs
/// the registry. The head binds the real behaviour once the layout is built. Until then <see cref="Show"/> returns false.
/// </summary>
public sealed class LatePaneNavigator : IPaneNavigator
{
    private Func<string, bool>? _show;

    /// <summary>Sets what <see cref="Show"/> does. A later call replaces the earlier one.</summary>
    public void Bind(Func<string, bool> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        _show = show;
    }

    /// <inheritdoc />
    public bool Show(string paneKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneKind);
        return _show?.Invoke(paneKind) ?? false;
    }
}
