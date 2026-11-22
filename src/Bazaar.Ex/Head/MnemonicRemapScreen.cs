using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Bazaar.Client.Input;

namespace Bazaar.Ex.Head;

/// <summary>One command on the remap screen: its title, the mnemonic it answers to now, and whether the person has remapped it.</summary>
public sealed record MnemonicScreenRow(string CommandId, string Title, string Mnemonic, bool Remapped, bool Locked)
{
    public override string ToString() => Remapped ? $"{Mnemonic}  {Title}  (remapped)" : $"{Mnemonic}  {Title}";
}

/// <summary>
/// The remap screen: every command with its current mnemonic. Selecting a command opens a <see cref="MnemonicRemapView"/> for it,
/// and Reset drops the person's remap so the default applies again. The screen never saves anything: each accepted change raises
/// <see cref="DocumentChanged"/> with the new override document, and the head saves it through its
/// <see cref="IMnemonicOverrideStore"/>. A command the company locked can be viewed but not changed unless the actor is the owner.
/// Escape raises <see cref="Closed"/>.
/// </summary>
public sealed class MnemonicRemapScreen : StackPanel
{
    private readonly IReadOnlyList<MnemonicAssignment> _defaults;
    private readonly IReadOnlyList<KeyStroke> _paletteKeys;
    private readonly bool _actorIsOwner;
    private readonly ListBox _list = new();
    private readonly ContentControl _editor = new();
    private readonly Button _reset = new() { Content = "Reset to default" };
    private readonly TextBlock _status = new();
    private MnemonicOverrideDocument _document;

    public MnemonicRemapScreen(
        IEnumerable<MnemonicAssignment> baseAssignments,
        MnemonicOverrideDocument current,
        IEnumerable<KeyStroke> paletteKeys,
        bool actorIsOwner = false)
    {
        ArgumentNullException.ThrowIfNull(baseAssignments);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(paletteKeys);

        // The primary default of each command: the same set the remapper accepts, one entry per command.
        _defaults = baseAssignments
            .Where(a => a.Tier != MnemonicTier.Person && !a.Alias)
            .GroupBy(a => a.CommandId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        _document = current;
        _paletteKeys = paletteKeys.ToList();
        _actorIsOwner = actorIsOwner;

        Orientation = Orientation.Vertical;
        // Tunnel, so the list (which handles Escape itself) does not swallow it before the screen sees it.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _reset.Click += (_, _) => ResetSelected();

        Children.Add(new TextBlock { Text = "Remap commands" });
        Children.Add(_list);
        Children.Add(_editor);
        Children.Add(_reset);
        Children.Add(_status);
        Refresh();
    }

    /// <summary>Raised with the new override document after an accepted remap or reset. The head saves it.</summary>
    public event EventHandler<MnemonicOverrideDocument>? DocumentChanged;

    /// <summary>Raised when the user presses Escape on the list or on a remap prompt that did not take it.</summary>
    public event EventHandler? Closed;

    /// <summary>The override document as of the last accepted change.</summary>
    public MnemonicOverrideDocument Document => _document;

    /// <summary>Every command on the screen, sorted by title.</summary>
    public IReadOnlyList<MnemonicScreenRow> Rows => _list.ItemsSource is IEnumerable<MnemonicScreenRow> rows ? rows.ToList() : [];

    /// <summary>The command selected in the list, or null when none is.</summary>
    public string? SelectedCommandId => (_list.SelectedItem as MnemonicScreenRow)?.CommandId;

    /// <summary>The status line: the refusal reason or the last change, empty when there is none.</summary>
    public string Status => _status.Text ?? string.Empty;

    /// <summary>Selects a command by id. Returns false when the screen does not list it.</summary>
    public bool Select(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        var row = Rows.FirstOrDefault(r => r.CommandId == commandId);
        if (row is null)
        {
            return false;
        }

        _list.SelectedItem = row;
        return true;
    }

    /// <summary>Moves keyboard focus to the list, as the head does when the screen opens.</summary>
    public void FocusList() => _list.Focus();

    private void Refresh()
    {
        var selected = SelectedCommandId;
        var rows = _defaults
            .Select(d =>
            {
                var remap = _document.Overrides.FirstOrDefault(o => o.CommandId == d.CommandId);
                var locked = d.Tier == MnemonicTier.CompanyLocked && !_actorIsOwner;
                return new MnemonicScreenRow(d.CommandId, d.Title, remap?.Mnemonic ?? d.Mnemonic, remap is not null, locked);
            })
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.CommandId, StringComparer.Ordinal)
            .ToList();

        _list.ItemsSource = rows;
        if (selected is not null)
        {
            Select(selected);
        }

        UpdateEditor();
    }

    private void OnSelectionChanged()
    {
        _status.Text = string.Empty;
        UpdateEditor();
    }

    private void UpdateEditor()
    {
        if (_list.SelectedItem is not MnemonicScreenRow row)
        {
            _editor.Content = null;
            _reset.IsEnabled = false;
            return;
        }

        _reset.IsEnabled = row.Remapped && !row.Locked;
        if (row.Locked)
        {
            _editor.Content = new TextBlock { Text = $"'{row.Title}' is locked by your company; only the owner can change it." };
            return;
        }

        var view = new MnemonicRemapView(_defaults, _document, row.CommandId, row.Title, _paletteKeys, _actorIsOwner);
        view.Applied += (_, document) => Accept(document, $"'{row.Title}' is saved.");
        view.Cancelled += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _editor.Content = view;
    }

    private void ResetSelected()
    {
        if (_list.SelectedItem is not MnemonicScreenRow row || !row.Remapped || row.Locked)
        {
            return;
        }

        var result = MnemonicRemapper.Reset(_document, row.CommandId);
        if (!result.Accepted)
        {
            _status.Text = result.Reason ?? string.Empty;
            return;
        }

        Accept(result.Document, $"'{row.Title}' is back to its default.");
    }

    private void Accept(MnemonicOverrideDocument document, string message)
    {
        _document = document;
        Refresh();
        _status.Text = message;
        DocumentChanged?.Invoke(this, _document);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // A remap prompt reports its own Escape through Cancelled, so only keys from outside the prompt count here.
        if (e.Source is Visual source && source.GetVisualAncestors().Contains(_editor))
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
