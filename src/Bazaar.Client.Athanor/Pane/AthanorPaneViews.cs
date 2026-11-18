using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>The pane body of the Athanor editor. The pane module registers it with the pane registry.</summary>
internal static class AthanorPaneViews
{
    internal static Control Editor(AthanorPaneViewModel pane)
    {
        if (pane.IsEmpty)
        {
            return new TextBlock { Text = AthanorPaneViewModel.EmptyMessage, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        }

        var tabs = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }),
            ItemTemplate = new FuncDataTemplate<AthanorTabItem>((tab, _) => TabButton(pane, tab)),
        };
        AutomationProperties.SetName(tabs, "Open documents");

        var banner = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        var keepMine = new Button { Content = "Keep mine", HorizontalAlignment = HorizontalAlignment.Left };
        keepMine.Click += (_, _) => _ = pane.KeepMineAsync();
        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(save, "Save document");
        save.Click += (_, _) => _ = pane.SaveActiveAsync();

        var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetName(editor, "Document text");

        var updating = false;
        editor.TextChanged += (_, _) =>
        {
            if (!updating)
            {
                pane.EditActive(editor.Text ?? string.Empty);
            }
        };

        void Render()
        {
            updating = true;
            try
            {
                tabs.ItemsSource = pane.Tabs;
                var active = pane.ActiveTab;
                editor.IsVisible = active is not null;
                editor.IsReadOnly = active?.ReadOnly ?? true;
                if (!string.Equals(editor.Text ?? string.Empty, pane.ActiveContent, StringComparison.Ordinal))
                {
                    editor.Text = pane.ActiveContent;
                }

                save.IsEnabled = active is { Dirty: true };
                var conflict = pane.ActiveConflict;
                banner.IsVisible = conflict is not null || pane.ActiveUnavailableReason is not null;
                banner.Text = conflict is not null
                    ? "The server has a newer revision of this document."
                    : pane.ActiveUnavailableReason is { } reason ? "Not saved: " + reason : string.Empty;
                keepMine.IsVisible = conflict is not null;
            }
            finally
            {
                updating = false;
            }
        }

        void OnChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Render();
            }
            else
            {
                Dispatcher.UIThread.Post(Render);
            }
        }

        var root = new DockPanel { LastChildFill = true };
        var header = new StackPanel { Spacing = 4, Children = { tabs, save, banner, keepMine } };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(editor);

        root.AttachedToVisualTree += (_, _) =>
        {
            pane.Changed += OnChanged;
            Render();
        };
        root.DetachedFromVisualTree += (_, _) => pane.Changed -= OnChanged;
        Render();
        return root;
    }

    private static Button TabButton(AthanorPaneViewModel pane, AthanorTabItem? tab)
    {
        var button = new Button
        {
            Content = tab?.Label ?? string.Empty,
            FontWeight = tab is { Active: true } ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (tab is not null)
        {
            AutomationProperties.SetName(button, tab.Label);
            button.Click += (_, _) => pane.Activate(tab.Id);
        }

        return button;
    }
}
