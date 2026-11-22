using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Org.Panes;

/// <summary>The Org pane body: a lens selector, the reports_to tree and the loan rows. Read-only; colours come from theme tokens.</summary>
internal static class OrgPaneViews
{
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    internal static StackPanel Org(OrgPaneViewModel org)
    {
        var body = new StackPanel { Spacing = 8 };

        var lens = new ComboBox
        {
            ItemsSource = org.Lenses.Select(l => l.Name).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(lens, "Org lens");
        var content = new StackPanel { Spacing = 6 };

        var notice = new TextBlock { Text = OrgPaneViewModel.NoAuthorityNotice, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        SetThemeBrush(notice, TextBlock.ForegroundProperty, _secondaryTextToken);

        void Render()
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock { Text = "Reports to", FontWeight = FontWeight.SemiBold });
            if (org.Tree.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "No agents in this scope.", Opacity = 0.7 });
            }

            foreach (var row in org.Tree)
            {
                var text = new TextBlock
                {
                    Text = row.Label,
                    Margin = new Thickness(16 * row.Depth, 0, 0, 0),
                    Opacity = row.IsRepeat ? 0.6 : 1,
                    FontStyle = row.IsRepeat ? FontStyle.Italic : FontStyle.Normal,
                };
                AutomationProperties.SetName(text, row.Label);
                content.Children.Add(text);
            }

            content.Children.Add(new TextBlock { Text = "Loans", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            if (org.Loans.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "No loans in this scope.", Opacity = 0.7 });
            }

            foreach (var loan in org.Loans)
            {
                var text = new TextBlock { Text = loan.Line, TextWrapping = TextWrapping.Wrap, Opacity = loan.IsEnded ? 0.5 : 1 };
                AutomationProperties.SetName(text, loan.Line);
                content.Children.Add(text);
            }
        }

        lens.SelectionChanged += (_, _) =>
        {
            if (lens.SelectedIndex >= 0 && lens.SelectedIndex < org.Lenses.Count)
            {
                org.SelectLens(org.Lenses[lens.SelectedIndex].ProjectId);
                Render();
            }
        };

        Render();
        body.Children.Add(lens);
        body.Children.Add(notice);
        body.Children.Add(content);
        return body;
    }

    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
