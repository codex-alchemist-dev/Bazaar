using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Health.Panes;

/// <summary>The pane bodies of the health and analytics panes. Each pane module registers its body with the pane registry.</summary>
internal static class HealthPaneViews
{
    private static readonly IBrush _secondaryText = Brushes.Gray;

    internal static StackPanel Health(HealthPaneViewModel health)
    {
        var body = new StackPanel { Spacing = 10 };
        if (health.Projects.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No projects reported.", Foreground = _secondaryText });
            return body;
        }

        foreach (var project in health.Projects)
        {
            body.Children.Add(new TextBlock { Text = $"{project.ProjectId} · {project.Line}", FontWeight = FontWeight.SemiBold });
            foreach (var reason in project.Reasons)
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"{reason.Kind}: {reason.Message}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 0, 0, 0),
                });
            }
        }

        return body;
    }

    internal static StackPanel Analytics(AnalyticsPaneViewModel analytics)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"{analytics.ProjectId} · last {analytics.WindowDays} days",
            Foreground = _secondaryText,
        });

        if (analytics.Series.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No trend data yet.", Foreground = _secondaryText });
        }

        foreach (var series in analytics.Series)
        {
            body.Children.Add(new TextBlock { Text = $"{series.Metric} ({series.Unit})", FontWeight = FontWeight.SemiBold });
            foreach (var point in series.Points)
            {
                // A point without a value is a gap, shown below as a range. It is never drawn as zero.
                if (point.Value is { } value)
                {
                    body.Children.Add(Line($"{point.Day:yyyy-MM-dd}: {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
                }
            }

            foreach (var gap in series.Gaps)
            {
                body.Children.Add(Line($"no data {gap.From:yyyy-MM-dd} to {gap.To:yyyy-MM-dd}", _secondaryText));
            }
        }

        if (analytics.MoraleSignals.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Morale signals", FontWeight = FontWeight.SemiBold });
            foreach (var signal in analytics.MoraleSignals)
            {
                body.Children.Add(Line($"{signal.Signal}: {signal.Value}"));
            }
        }

        if (analytics.AgentView is { } view)
        {
            // Display only. The framing says what the view is for; agents appear by name, with no rank or score.
            body.Children.Add(new TextBlock { Text = "Per-agent view", FontWeight = FontWeight.SemiBold });
            body.Children.Add(new TextBlock { Text = view.Framing, TextWrapping = TextWrapping.Wrap, Foreground = _secondaryText });
            foreach (var agent in view.Agents)
            {
                body.Children.Add(Line(agent.DisplayName));
                foreach (var metric in agent.Metrics)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = $"{metric.Metric}: {metric.Value}",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(32, 0, 0, 0),
                    });
                }
            }
        }

        return body;
    }

    private static TextBlock Line(string text, IBrush? foreground = null) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 0, 0),
            Foreground = foreground,
        };
}
