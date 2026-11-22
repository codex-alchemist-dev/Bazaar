using Avalonia.Controls;
using Avalonia.Input;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>
/// An ordered list of named focus scopes for one window (docs/design/41c-window-chrome-host.md): for example the start
/// dashboard, the group chat list and the composer. A mnemonic moves focus to the next or previous scope, so the
/// keyboard reaches every area without tabbing through all of it. The head registers scopes in reading order.
/// </summary>
public sealed class FocusScopes
{
    private readonly List<(string Name, Control Target)> _scopes = [];
    private int _current = -1;

    /// <summary>The scope names, in order.</summary>
    public IReadOnlyList<string> Names => _scopes.Select(s => s.Name).ToList();

    /// <summary>The name of the scope focus last moved to, or null.</summary>
    public string? Current => _current < 0 ? null : _scopes[_current].Name;

    /// <summary>Adds a scope after the existing ones; a repeated name replaces its target in place.</summary>
    public void Register(string name, Control target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(target);
        int at = _scopes.FindIndex(s => s.Name == name);
        if (at >= 0)
        {
            _scopes[at] = (name, target);
        }
        else
        {
            _scopes.Add((name, target));
        }
    }

    /// <summary>Moves focus to the next visible, enabled scope, wrapping; returns its name, or null when none can take focus.</summary>
    public string? FocusNext() => Move(1);

    /// <summary>Moves focus to the previous visible, enabled scope, wrapping.</summary>
    public string? FocusPrevious() => Move(-1);

    /// <summary>Moves focus to a scope by name.</summary>
    public bool FocusScope(string name)
    {
        int at = _scopes.FindIndex(s => s.Name == name);
        return at >= 0 && TryFocus(at);
    }

    private string? Move(int step)
    {
        int count = _scopes.Count;
        for (int i = 1; i <= count; i++)
        {
            int at = (((_current < 0 ? (step > 0 ? -1 : 0) : _current) + (step * i)) % count + count) % count;
            if (TryFocus(at))
            {
                return _scopes[at].Name;
            }
        }

        return null;
    }

    private bool TryFocus(int at)
    {
        Control target = _scopes[at].Target;
        if (!target.IsVisible || !target.IsEnabled || !target.Focusable || !target.Focus())
        {
            return false;
        }

        _current = at;
        return true;
    }
}
