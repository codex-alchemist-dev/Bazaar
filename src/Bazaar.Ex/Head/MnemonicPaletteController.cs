using Bazaar.Client.Core.Input;

namespace Bazaar.Ex.Head;

/// <summary>
/// Drives the palette view from the router's palette actions, so the keys the router consumes in palette mode
/// (typed characters, Backspace, up, down, Enter, Escape) reach the view even though the router handles them before any control.
/// The shell supplies <paramref name="show"/> and <paramref name="hide"/> to place the view; the controller never decides layout.
/// A chosen row is reported through <see cref="CommandChosen"/>.
/// </summary>
public sealed class MnemonicPaletteController
{
    private readonly Func<IReadOnlyList<MnemonicListRow>> _rows;
    private readonly Action<MnemonicPaletteView> _show;
    private readonly Action<MnemonicPaletteView> _hide;
    private MnemonicPaletteView? _view;

    public MnemonicPaletteController(
        Func<IReadOnlyList<MnemonicListRow>> rows,
        Action<MnemonicPaletteView> show,
        Action<MnemonicPaletteView> hide)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
    }

    /// <summary>Raised with the command id of the row the user chose. The palette has closed by then.</summary>
    public event EventHandler<string>? CommandChosen;

    /// <summary>True while the palette is open.</summary>
    public bool IsOpen => _view is not null;

    /// <summary>The open view, or null when the palette is closed.</summary>
    public MnemonicPaletteView? View => _view;

    /// <summary>Applies one router action. Actions that are not palette actions are ignored.</summary>
    public void Handle(RouterAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Kind)
        {
            case RouterActionKind.OpenPalette:
                Open();
                break;
            case RouterActionKind.ClosePalette:
                Close();
                break;
            case RouterActionKind.PaletteInput when _view is not null && action.Character is { } character:
                _view.Query += character;
                break;
            case RouterActionKind.PaletteBackspace when _view is { Query.Length: > 0 }:
                _view.Query = _view.Query[..^1];
                break;
            case RouterActionKind.PaletteSelectNext:
                _view?.SelectNext();
                break;
            case RouterActionKind.PaletteSelectPrevious:
                _view?.SelectPrevious();
                break;
            case RouterActionKind.PaletteSubmit when _view is not null:
                Submit();
                break;
        }
    }

    private void Open()
    {
        if (_view is not null)
        {
            return;
        }

        _view = new MnemonicPaletteView(_rows());
        _view.Dismissed += (_, _) => Close();
        _show(_view);
    }

    private void Submit()
    {
        var view = _view!;
        var chosen = view.Selected?.CommandId;
        Close();
        if (chosen is not null)
        {
            CommandChosen?.Invoke(this, chosen);
        }
    }

    private void Close()
    {
        if (_view is not { } view)
        {
            return;
        }

        _view = null;
        _hide(view);
    }
}
