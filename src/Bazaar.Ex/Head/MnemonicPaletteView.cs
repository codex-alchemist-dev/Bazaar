using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Bazaar.Ex.Head;

/// <summary>
/// The palette's view: a query box over the filtered mnemonic list. Typing narrows the rows with <see cref="MnemonicPalette"/>;
/// Up and Down move the selection; Enter raises <see cref="CommandSelected"/> for the chosen row; Escape raises <see cref="Dismissed"/>.
/// It holds no command logic, so the head decides what a selection does.
/// </summary>
public sealed class MnemonicPaletteView : StackPanel
{
    private readonly IReadOnlyList<MnemonicListRow> _rows;
    private readonly TextBox _query = new();
    private readonly ListBox _list = new();
    private IReadOnlyList<MnemonicListRow> _shown;

    public MnemonicPaletteView(IEnumerable<MnemonicListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows.ToList();
        _shown = _rows;

        Orientation = Orientation.Vertical;
        _query.PlaceholderText = "Mnemonic or command";
        _query.KeyDown += OnQueryKeyDown;
        _query.TextChanged += (_, _) => Refresh();
        Children.Add(_query);
        Children.Add(_list);

        Refresh();
    }

    /// <summary>Moves keyboard focus to the query box, as the head does when the palette opens.</summary>
    public void FocusQuery() => _query.Focus();

    /// <summary>Raised with the command id of the row the user chose.</summary>
    public event EventHandler<string>? CommandSelected;

    /// <summary>Raised when the user dismisses the palette with Escape.</summary>
    public event EventHandler? Dismissed;

    /// <summary>The rows currently shown, in the order the list displays them.</summary>
    public IReadOnlyList<MnemonicListRow> Shown => _shown;

    /// <summary>The row Enter would choose, or null when nothing matches.</summary>
    public MnemonicListRow? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    /// <summary>The text the user has typed into the query box.</summary>
    public string Query
    {
        get => _query.Text ?? string.Empty;
        set
        {
            _query.Text = value;
            Refresh();
        }
    }

    private void Refresh()
    {
        _shown = MnemonicPalette.Filter(_rows, Query);
        _list.ItemsSource = _shown.Select(row => $"{row.Mnemonic}  {row.Title}").ToList();
        _list.SelectedIndex = _shown.Count > 0 ? 0 : -1;
    }

    /// <summary>Moves the selection down one row, stopping at the last.</summary>
    public void SelectNext()
    {
        if (_shown.Count > 0)
        {
            _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _shown.Count - 1);
        }
    }

    /// <summary>Moves the selection up one row, stopping at the first.</summary>
    public void SelectPrevious()
    {
        if (_shown.Count > 0)
        {
            _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
        }
    }

    /// <summary>Raises <see cref="CommandSelected"/> for the selected row. Returns false when nothing is selected.</summary>
    public bool Submit()
    {
        if (Selected is not { } row)
        {
            return false;
        }

        CommandSelected?.Invoke(this, row.CommandId);
        return true;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _shown.Count > 0:
                SelectNext();
                e.Handled = true;
                break;
            case Key.Up when _shown.Count > 0:
                SelectPrevious();
                e.Handled = true;
                break;
            case Key.Enter when Submit():
                e.Handled = true;
                break;
            case Key.Escape:
                Dismissed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }
}
