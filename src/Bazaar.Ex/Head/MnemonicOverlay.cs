using Avalonia.Controls;

namespace Bazaar.Ex.Head;

/// <summary>
/// Shows palette and remap content in its own small window that belongs to <paramref name="owner"/>. A head that has no
/// overlay layer of its own passes <see cref="Show"/> and <see cref="Hide"/> to <see cref="MnemonicHeadHost"/>.
/// </summary>
public sealed class MnemonicOverlay
{
    private readonly Window _owner;
    private readonly Dictionary<Control, Window> _open = new();

    public MnemonicOverlay(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>Opens <paramref name="content"/> in an overlay window owned by the head's window.</summary>
    public void Show(Control content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_open.ContainsKey(content))
        {
            return;
        }

        var window = new Window { Content = content, Title = string.Empty, CanResize = false, SizeToContent = SizeToContent.WidthAndHeight };
        _open[content] = window;
        window.Closed += (_, _) => _open.Remove(content);
        window.Show(_owner);
    }

    /// <summary>Closes the overlay that holds <paramref name="content"/>, if it is open.</summary>
    public void Hide(Control content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_open.Remove(content, out var window))
        {
            window.Close();
        }
    }
}
