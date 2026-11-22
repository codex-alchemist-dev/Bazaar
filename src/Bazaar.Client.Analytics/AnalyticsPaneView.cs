using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Analytics;

/// <summary>The pane body: the company line, then each agent's figures and what would help the company.</summary>
public static class AnalyticsPaneView
{
    public static StackPanel Create(AnalyticsPaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = pane.CompanySummary, FontWeight = FontWeight.SemiBold });
        foreach (var agent in pane.Agents)
        {
            body.Children.Add(new TextBlock { Text = $"{agent.AgentId}: {agent.TasksPerWeek} tasks/week, reviews passed {agent.ReviewPassRate}, reverts {agent.RevertRate}" });
            foreach (var hint in agent.Hints)
            {
                body.Children.Add(new TextBlock { Text = "Could help: " + hint });
            }
        }

        return body;
    }
}
