using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// <see cref="IPromptWidgets"/> on Terminal.Gui (doc 58 work package 2): each question is a modal dialog in the fixed dark look, run
/// on the application the host owns, so the wizard prompter above it is the same one the console path uses. OK, Enter on a field
/// or list, and the default button accept; Esc, Cancel and Ctrl+C return null, which the wizard reads as "stop before anything is
/// written". A secret is masked while typed and cleared from the field as the dialog closes.
/// </summary>
public sealed class TerminalGuiPromptWidgets(IApplication application) : IPromptWidgets
{
    private const int _maxListRows = 8;

    public string? TextEntry(string prompt, string? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var field = new TextField { X = 0, Y = 0, Width = Dim.Fill(), Text = defaultValue ?? "" };
        var accepted = Ask(prompt, [field], field);
        var text = field.Text;
        field.Dispose();
        return accepted ? (text.Length == 0 ? defaultValue ?? "" : text) : null;
    }

    public string? SecretEntry(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var field = new TextField { X = 0, Y = 0, Width = Dim.Fill(), Secret = true };
        try
        {
            var accepted = Ask(prompt, [field], field);
            return accepted ? field.Text : null;
        }
        finally
        {
            // Nothing typed stays in the field (or its undo history) after the call; the caller owns the returned text.
            field.Text = "";
            field.ClearHistoryChanges();
            field.Dispose();
        }
    }

    public string? PickOne(string prompt, IReadOnlyList<string> options, string? defaultOption)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            return null;
        }

        var list = NewList(options);
        var start = defaultOption is null ? 0 : Math.Max(0, options.ToList().IndexOf(defaultOption));
        list.SelectedItem = start;
        var accepted = Ask(prompt, [list], list);
        var chosen = list.SelectedItem;
        list.Dispose();
        return accepted && chosen is { } index && index >= 0 && index < options.Count ? options[index] : null;
    }

    public IReadOnlyList<string>? PickMany(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);

        var list = NewList(options);
        list.MarkMultiple = true;
        list.ShowMarks = true;
        for (var index = 0; index < options.Count; index++)
        {
            if (defaults.Contains(options[index], StringComparer.Ordinal))
            {
                list.Source?.SetMark(index, true);
            }
        }

        var accepted = Ask(prompt + "  (Space marks)", [list], list);
        var picked = accepted
            ? list.GetAllMarkedItems().Where(index => index >= 0 && index < options.Count).Order().Select(index => options[index]).ToList()
            : null;
        list.Dispose();
        return picked;
    }

    public bool? YesNo(string prompt, bool defaultYes)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var answer = (bool?)null;
        var yes = new Button { Text = "Yes", IsDefault = defaultYes };
        var no = new Button { Text = "No", IsDefault = !defaultYes };
        yes.Accepting += (_, _) => answer = true;
        no.Accepting += (_, _) => answer = false;

        using var dialog = NewDialog(prompt, out _);
        dialog.AddButton(yes);
        dialog.AddButton(no);

        // Enter takes the focused button, and the default answer starts focused.
        var first = defaultYes ? yes : no;
        dialog.Initialized += (_, _) => first.SetFocus();
        dialog.IsModalChanged += (_, changed) =>
        {
            if (changed.Value)
            {
                first.SetFocus();
            }
        };
        Run(dialog);
        return answer;
    }

    private static ListView NewList(IReadOnlyList<string> options)
    {
        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Math.Max(1, Math.Min(options.Count, _maxListRows)),
        };
        list.SetSource(new ObservableCollection<string>(options));

        // Nothing is selected until a key moves, so Space would do nothing on the first press.
        list.SelectedItem = options.Count > 0 ? 0 : null;
        return list;
    }

    /// <summary>Runs a dialog with OK and Cancel around <paramref name="body"/>. True only when OK (or Enter) accepted it.</summary>
    private bool Ask(string prompt, View[] body, View focus)
    {
        var accepted = false;
        var ok = new Button { Text = "OK", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        ok.Accepting += (_, _) => accepted = true;

        using var dialog = NewDialog(prompt, out var label);
        foreach (var view in body)
        {
            view.Y = Pos.Bottom(label);
            dialog.Add(view);

            // Enter on the field or list accepts. (The dialog's own default button is its last one, Cancel.)
            view.Accepting += (_, accepting) =>
            {
                accepted = true;
                accepting.Handled = true;
                application.RequestStop(dialog);
            };
        }

        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        // A dialog starts on its last button; the question's own view takes focus once the dialog is on top.
        dialog.Initialized += (_, _) => focus.SetFocus();
        dialog.IsModalChanged += (_, changed) =>
        {
            if (changed.Value)
            {
                focus.SetFocus();
            }
        };
        Run(dialog);
        return accepted;
    }

    private Dialog NewDialog(string prompt, out Label label)
    {
        var width = Math.Clamp(application.Screen.Width - 4, 40, 76);
        var textRows = WrappedRows(prompt, width - 4);
        var dialog = new Dialog
        {
            Title = "Setup",
            Width = width,
            Height = Dim.Auto(),
        };
        dialog.SetScheme(FixedLook.Body);
        label = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = textRows, Text = prompt };
        dialog.Add(label);
        return dialog;
    }

    private void Run(Dialog dialog)
    {
        // Ctrl+C is the wizard's cancel; Esc cancels the dialog on its own.
        dialog.KeyDown += (_, key) =>
        {
            if (KeyTranslator.Translate(key) is { Kind: TerminalKeyKind.Cancel })
            {
                key.Handled = true;
                application.RequestStop(dialog);
            }
        };
        application.Run(dialog);
    }

    private static int WrappedRows(string text, int width)
    {
        var rows = 0;
        foreach (var line in text.Split('\n'))
        {
            rows += Math.Max(1, (line.Length + width - 1) / Math.Max(1, width));
        }

        return rows;
    }
}
