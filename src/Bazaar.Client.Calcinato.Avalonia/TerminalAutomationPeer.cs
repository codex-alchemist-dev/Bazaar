using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Bazaar.Client.Calcinato.Panes;

namespace Bazaar.Client.Calcinato.Avalonia;

/// <summary>
/// The screen-reader view of a terminal (doc 55, section 5.2): an edit-type element named "Terminal" whose value is the
/// visible text, one line per row. The value follows the accessible text model, which the pane rate-limits, and the
/// control raises a value-changed event when it moves, so the live region announces new output.
/// </summary>
public sealed class TerminalAutomationPeer : ControlAutomationPeer, IValueProvider
{
    /// <summary>The accessible name of every terminal.</summary>
    public const string Name = "Terminal";

    private readonly TerminalControl _control;

    /// <summary>Creates the peer for a terminal control.</summary>
    /// <param name="control">The control the peer describes.</param>
    public TerminalAutomationPeer(TerminalControl control)
        : base(control)
    {
        _control = control;
    }

    /// <summary>The value a screen reader reads: the visible rows joined by line breaks.</summary>
    /// <param name="text">The accessible text model of the pane.</param>
    public static string ValueOf(TerminalAccessibleText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join('\n', text.VisibleRows);
    }

    /// <summary>The text value. Read-only: the terminal's text changes only by program output.</summary>
    public string Value => ValueOf(_control.Pane.Accessible);

    /// <summary>Always true. Input reaches the shell as keys, not as a value.</summary>
    public bool IsReadOnly => true;

    /// <summary>Not supported. Screen readers type through keys and paste, not by setting the value.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void SetValue(string? value) => throw new NotSupportedException("A terminal takes input as keys, not as a value.");

    /// <inheritdoc />
    protected override string GetNameCore() => Name;

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
}
