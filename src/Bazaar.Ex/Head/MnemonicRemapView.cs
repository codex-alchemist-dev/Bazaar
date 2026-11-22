using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Bazaar.Client.Input;

namespace Bazaar.Ex.Head;

/// <summary>
/// The remap prompt for one command: the person types the mnemonic they want and presses Enter. The view asks
/// <see cref="MnemonicRemapper"/>; an accepted remap raises <see cref="Applied"/> with the new override document (the head saves it),
/// a refused one stays open and shows the reason. Escape raises <see cref="Cancelled"/>.
/// </summary>
public sealed class MnemonicRemapView : StackPanel
{
    private readonly IReadOnlyList<MnemonicAssignment> _baseAssignments;
    private readonly IReadOnlyList<KeyStroke> _paletteKeys;
    private readonly string _commandId;
    private readonly bool _actorIsOwner;
    private readonly TextBox _input = new();
    private readonly TextBlock _status = new();
    private MnemonicOverrideDocument _document;

    public MnemonicRemapView(
        IEnumerable<MnemonicAssignment> baseAssignments,
        MnemonicOverrideDocument current,
        string commandId,
        string commandTitle,
        IEnumerable<KeyStroke> paletteKeys,
        bool actorIsOwner = false)
    {
        ArgumentNullException.ThrowIfNull(baseAssignments);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(commandTitle);
        ArgumentNullException.ThrowIfNull(paletteKeys);

        _baseAssignments = baseAssignments.ToList();
        _document = current;
        _commandId = commandId;
        _paletteKeys = paletteKeys.ToList();
        _actorIsOwner = actorIsOwner;

        Orientation = Orientation.Vertical;
        _input.PlaceholderText = "New mnemonic";
        _input.KeyDown += OnKeyDown;
        Children.Add(new TextBlock { Text = $"Remap: {commandTitle}" });
        Children.Add(_input);
        Children.Add(_status);
    }

    /// <summary>Raised with the new override document after an accepted remap.</summary>
    public event EventHandler<MnemonicOverrideDocument>? Applied;

    /// <summary>Raised when the user cancels with Escape.</summary>
    public event EventHandler? Cancelled;

    /// <summary>The override document as of the last accepted remap.</summary>
    public MnemonicOverrideDocument Document => _document;

    /// <summary>The refusal reason currently shown, or null when none is.</summary>
    public string? Reason { get; private set; }

    /// <summary>The mnemonic text the user has typed.</summary>
    public string Text
    {
        get => _input.Text ?? string.Empty;
        set => _input.Text = value;
    }

    /// <summary>Moves keyboard focus to the input, as the head does when the prompt opens.</summary>
    public void FocusInput() => _input.Focus();

    /// <summary>Tries the typed mnemonic; the Enter key calls this.</summary>
    public bool Submit()
    {
        var result = MnemonicRemapper.TryRemap(_baseAssignments, _document, _commandId, Text, _paletteKeys, _actorIsOwner);
        Reason = result.Reason;
        _status.Text = result.Reason ?? string.Empty;
        if (!result.Accepted)
        {
            return false;
        }

        _document = result.Document;
        Applied?.Invoke(this, _document);
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Submit();
                e.Handled = true;
                break;
            case Key.Escape:
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }
}
