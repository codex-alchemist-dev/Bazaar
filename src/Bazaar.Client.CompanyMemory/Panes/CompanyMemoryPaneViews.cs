using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.CompanyMemory.Panes;

/// <summary>The pane body of the company memory decision chain. The module registers it with the pane registry.</summary>
internal static class CompanyMemoryPaneViews
{
    internal static StackPanel DecisionChain(CompanyMemoryPaneViewModel memory)
    {
        var body = new StackPanel { Spacing = 10 };
        if (memory.Decisions.Count == 0 && memory.Links.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No decisions recorded.", Opacity = 0.7 });
            return body;
        }

        body.Children.Add(new TextBlock { Text = "Decisions", FontWeight = FontWeight.SemiBold });
        foreach (var decision in memory.Decisions)
        {
            body.Children.Add(new TextBlock
            {
                Text = decision.Line,
                TextWrapping = TextWrapping.Wrap,
                Opacity = decision.IsSuperseded ? 0.6 : 1.0,
                Margin = new Thickness(16, 0, 0, 0),
            });
            body.Children.Add(new TextBlock
            {
                Text = $"{decision.Path} · {decision.DecidedText}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(16, 0, 0, 0),
            });
        }

        body.Children.Add(new TextBlock { Text = "Chain", FontWeight = FontWeight.SemiBold });
        if (memory.Links.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No supersession links.", Opacity = 0.7, Margin = new Thickness(16, 0, 0, 0) });
        }

        foreach (var link in memory.Links)
        {
            body.Children.Add(new TextBlock { Text = link.Line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) });
            foreach (var missing in link.MissingSources)
            {
                body.Children.Add(new TextBlock
                {
                    Text = missing,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(32, 0, 0, 0),
                });
            }
        }

        return body;
    }
}
