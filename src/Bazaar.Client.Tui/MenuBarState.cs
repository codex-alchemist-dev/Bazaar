namespace Bazaar.Client.Tui;

/// <summary>
/// The menu bar's behaviour without a screen (doc 58 section 4): a visible bar moved with arrow keys and Enter, with optional single
/// letter shortcuts (no Ctrl chords) and a type-to-search over every entry. The menu bar stays the main way; shortcuts and search
/// are extras. Views draw from this state and feed it keys.
/// </summary>
public sealed class MenuBarState
{
    private readonly IReadOnlyList<MenuModel> _menus;

    public MenuBarState(IReadOnlyList<MenuModel> menus)
    {
        ArgumentNullException.ThrowIfNull(menus);
        if (menus.Count == 0)
        {
            throw new ArgumentException("A menu bar needs at least one menu.", nameof(menus));
        }

        _menus = menus;
    }

    public IReadOnlyList<MenuModel> Menus => _menus;

    /// <summary>Index of the highlighted menu.</summary>
    public int MenuIndex { get; private set; }

    /// <summary>True while the highlighted menu's items are shown.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Index of the highlighted item while open.</summary>
    public int ItemIndex { get; private set; }

    /// <summary>When false, letter shortcuts are ignored (the owner may turn them off; they are remappable through the models).</summary>
    public bool ShortcutsEnabled { get; set; } = true;

    private MenuModel Current => _menus[MenuIndex];

    /// <summary>
    /// Feeds one key. Returns the id of the screen or action chosen (a menu id, or an item id), or null when the key only moved the
    /// highlight.
    /// </summary>
    public string? Handle(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Left:
                MoveMenu(-1);
                return null;
            case TerminalKeyKind.Right:
                MoveMenu(1);
                return null;
            case TerminalKeyKind.Down:
                if (!IsOpen)
                {
                    Open();
                }
                else if (Current.Items.Count > 0)
                {
                    ItemIndex = (ItemIndex + 1) % Current.Items.Count;
                }

                return null;
            case TerminalKeyKind.Up:
                if (IsOpen && Current.Items.Count > 0)
                {
                    ItemIndex = (ItemIndex - 1 + Current.Items.Count) % Current.Items.Count;
                }

                return null;
            case TerminalKeyKind.Escape:
                IsOpen = false;
                return null;
            case TerminalKeyKind.Enter:
                return Activate();
            case TerminalKeyKind.Character when ShortcutsEnabled:
                return Shortcut(key.Value);
            default:
                return null;
        }
    }

    /// <summary>Every entry whose title contains <paramref name="query"/> (case-insensitive), as menu ids and item ids, in menu order.</summary>
    public IReadOnlyList<string> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var found = new List<string>();
        foreach (var menu in _menus)
        {
            if (menu.Items.Count == 0 && Matches(menu.Title, text))
            {
                found.Add(menu.Id);
            }

            found.AddRange(menu.Items.Where(item => Matches(item.Title, text)).Select(item => item.Id));
        }

        return found;
    }

    private static bool Matches(string title, string text) => title.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void MoveMenu(int step)
    {
        MenuIndex = (MenuIndex + step + _menus.Count) % _menus.Count;
        ItemIndex = 0;
        if (IsOpen && Current.Items.Count == 0)
        {
            IsOpen = false;
        }
    }

    private void Open()
    {
        if (Current.Items.Count > 0)
        {
            IsOpen = true;
            ItemIndex = 0;
        }
    }

    private string? Activate()
    {
        if (IsOpen && Current.Items.Count > 0)
        {
            var id = Current.Items[ItemIndex].Id;
            IsOpen = false;
            return id;
        }

        if (Current.Items.Count == 0)
        {
            return Current.Id;
        }

        Open();
        return null;
    }

    private string? Shortcut(char letter)
    {
        if (IsOpen)
        {
            var item = Current.Items.FirstOrDefault(candidate => SameLetter(candidate.Shortcut, letter));
            if (item is not null)
            {
                IsOpen = false;
                return item.Id;
            }
        }

        for (var index = 0; index < _menus.Count; index++)
        {
            if (!SameLetter(_menus[index].Shortcut, letter))
            {
                continue;
            }

            MenuIndex = index;
            ItemIndex = 0;
            IsOpen = false;
            if (_menus[index].Items.Count == 0)
            {
                return _menus[index].Id;
            }

            Open();
            return null;
        }

        return null;
    }

    private static bool SameLetter(char? shortcut, char letter) =>
        shortcut is { } value && char.ToUpperInvariant(value) == char.ToUpperInvariant(letter);
}
