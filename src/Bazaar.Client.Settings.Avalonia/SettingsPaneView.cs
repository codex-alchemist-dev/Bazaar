using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Bazaar.Client.Settings;

/// <summary>
/// The settings pane body: a search box, a status line, then one block per area with a row per setting. The list is rebuilt when
/// the pane's list changes; a row updates its own error, status and buttons so typing never loses focus.
/// </summary>
public static class SettingsPaneView
{
    private const string _errorTextToken = "Bazaar.brush.color.text.danger";

    /// <summary>Builds the view and starts loading the pane's settings.</summary>
    public static StackPanel Create(SettingsPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var root = new StackPanel { Spacing = 8 };
        var search = new TextBox { PlaceholderText = "Search settings", Text = pane.Search };
        AutomationProperties.SetName(search, "Search settings");
        search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                pane.Search = search.Text ?? string.Empty;
            }
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(status, "Settings status");
        var list = new StackPanel { Spacing = 12 };

        void Render()
        {
            status.Text = pane.StatusText;
            list.Children.Clear();
            foreach (var group in pane.Groups)
            {
                list.Children.Add(Group(group));
            }
        }

        pane.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Render();
            }
            else
            {
                Dispatcher.UIThread.Post(Render);
            }
        };

        Render();
        root.Children.Add(search);
        root.Children.Add(status);
        root.Children.Add(list);
        _ = pane.EnsureLoadedAsync();
        return root;
    }

    private static StackPanel Group(SettingGroup group)
    {
        var panel = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = group.Area, FontWeight = FontWeight.SemiBold, FontSize = 16 };
        AutomationProperties.SetName(heading, $"{group.Area} settings");
        panel.Children.Add(heading);
        foreach (var row in group.Rows)
        {
            panel.Children.Add(Row(row));
        }

        return panel;
    }

    private static Border Row(SettingRowViewModel row)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Secondary(row.Description));
        body.Children.Add(Secondary($"Default: {row.DefaultText}. Scope: {row.ScopeText}. Who may change: {row.WhoMayChangeText}."));
        if (row.ApprovalText is { } approval)
        {
            body.Children.Add(Secondary(approval));
        }

        if (row.ReadOnlyReason is { } readOnly)
        {
            body.Children.Add(Secondary(readOnly));
        }

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        error.Classes.Add("error");
        if (TryBrush(_errorTextToken) is { } errorBrush)
        {
            error.Foreground = errorBrush;
        }

        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var save = new Button { Content = "Save", IsEnabled = false };
        AutomationProperties.SetName(save, $"Save {row.Name}");
        var revert = new Button { Content = "Revert", IsEnabled = false };
        AutomationProperties.SetName(revert, $"Revert {row.Name}");

        var editor = Editor(row);
        AutomationProperties.SetName(editor, $"{row.Name} value");
        editor.IsEnabled = row.CanEdit;

        void Sync()
        {
            error.Text = row.Error;
            error.IsVisible = row.Error is not null;
            var text = row.Status ?? row.PendingNote;
            note.Text = text;
            note.IsVisible = text is not null;
            save.IsEnabled = row.CanSave;
            revert.IsEnabled = row.CanEdit && row.IsDirty;
        }

        row.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Sync();
            }
            else
            {
                Dispatcher.UIThread.Post(Sync);
            }
        };
        save.Click += async (_, _) => await row.SaveAsync().ConfigureAwait(true);
        revert.Click += (_, _) =>
        {
            row.Revert();
            Reset(editor, row);
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(editor);
        line.Children.Add(save);
        line.Children.Add(revert);
        body.Children.Add(Secondary($"Current: {row.CurrentText}{(row.IsDefault ? " (default)" : string.Empty)}"));
        body.Children.Add(line);
        body.Children.Add(error);
        body.Children.Add(note);
        Sync();

        var card = new Border { Padding = new Avalonia.Thickness(8), BorderThickness = new Avalonia.Thickness(1), Child = body };
        AutomationProperties.SetName(card, row.Name);
        return card;
    }

    private static Control Editor(SettingRowViewModel row)
    {
        switch (row.EditorKind)
        {
            case SettingEditorKind.Toggle:
                var toggle = new ToggleSwitch { IsChecked = string.Equals(row.Draft, "true", StringComparison.OrdinalIgnoreCase), OnContent = "On", OffContent = "Off" };
                toggle.IsCheckedChanged += (_, _) => row.Draft = toggle.IsChecked == true ? "true" : "false";
                return toggle;
            case SettingEditorKind.Choice:
                var combo = new ComboBox { ItemsSource = row.Choices, SelectedItem = row.Draft, MinWidth = 200 };
                combo.SelectionChanged += (_, _) => row.Draft = combo.SelectedItem as string ?? string.Empty;
                return combo;
            case SettingEditorKind.ReadOnlyValue:
                return new TextBlock { Text = row.CurrentText, TextWrapping = TextWrapping.Wrap };
            default:
                var box = new TextBox { Text = row.Draft, MinWidth = 240 };
                box.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        row.Draft = box.Text ?? string.Empty;
                    }
                };
                return box;
        }
    }

    private static void Reset(Control editor, SettingRowViewModel row)
    {
        switch (editor)
        {
            case ToggleSwitch toggle:
                toggle.IsChecked = string.Equals(row.Draft, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case ComboBox combo:
                combo.SelectedItem = row.Draft;
                break;
            case TextBox box:
                box.Text = row.Draft;
                break;
        }
    }

    private static TextBlock Secondary(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        return block;
    }

    private static IBrush? TryBrush(string key) =>
        Avalonia.Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
}
