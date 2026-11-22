using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Twin.Panes;

/// <summary>The pane body of the Digital Twin card. The pane module registers it with the pane registry.</summary>
internal static class TwinPaneViews
{
    // Theme tokens (one theme file drives every head). Views bind to these, never to a fixed colour.
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    internal static StackPanel Card(TwinPaneViewModel twin)
    {
        var body = new StackPanel { Spacing = 10 };
        if (twin.Projects.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No twin projects yet.", Opacity = 0.7 });
            return body;
        }

        foreach (var project in twin.Projects)
        {
            body.Children.Add(new TextBlock { Text = project.Title, FontWeight = FontWeight.SemiBold });
            body.Children.Add(Section("Facts"));
            if (project.Facts.Count == 0)
            {
                body.Children.Add(Muted("No facts yet."));
            }

            foreach (var fact in project.Facts)
            {
                body.Children.Add(FactRow(fact));
            }

            body.Children.Add(Section("Pages"));
            if (project.Pages.Count == 0)
            {
                body.Children.Add(Muted("No pages yet."));
            }

            foreach (var page in project.Pages)
            {
                body.Children.Add(PageBlock(page));
            }
        }

        return body;
    }

    /// <summary>One fact: a stale label when the server marked it stale, the headline, the meta line and its source refs.</summary>
    private static Border FactRow(TwinFactRow fact)
    {
        var content = new StackPanel { Spacing = 2 };
        if (fact.IsStale)
        {
            content.Children.Add(new TextBlock { Text = "STALE", FontWeight = FontWeight.SemiBold, Foreground = Brushes.OrangeRed });
        }

        content.Children.Add(new TextBlock { Text = fact.Line, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Muted(fact.Meta));
        if (fact.SourceRefs.Count == 0)
        {
            content.Children.Add(MissingSource());
        }

        foreach (var source in fact.SourceRefs)
        {
            content.Children.Add(Muted(source.Text));
        }

        return Framed(content);
    }

    /// <summary>One prose page: each sentence on its own line, with its source or an explicit missing-source row.</summary>
    private static StackPanel PageBlock(TwinPageRow page)
    {
        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(new TextBlock { Text = page.Page, FontWeight = FontWeight.SemiBold });
        foreach (var sentence in page.Sentences)
        {
            block.Children.Add(new TextBlock { Text = sentence.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) });
            block.Children.Add(sentence.HasSource
                ? Muted($"source {sentence.SourceRef}", new Thickness(16, 0, 0, 0))
                : MissingSource(new Thickness(16, 0, 0, 0)));
        }

        return block;
    }

    private static TextBlock MissingSource(Thickness? margin = null) =>
        new() { Text = "missing source", Foreground = Brushes.OrangeRed, FontSize = 12, Margin = margin ?? default };

    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.85 };

    private static TextBlock Muted(string text, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = margin ?? default };
        SetThemeBrush(block, TextBlock.ForegroundProperty, _secondaryTextToken);
        return block;
    }

    private static Border Framed(Control child)
    {
        var border = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = child,
        };
        SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
        return border;
    }

    /// <summary>
    /// Sets a brush property from a theme token. A token the active theme does not define falls back to transparent,
    /// so the view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
