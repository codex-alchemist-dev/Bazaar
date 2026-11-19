namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>Drill schedule. Bound from configuration; the scheduler never runs more often than <see cref="MinimumInterval"/>.</summary>
public sealed class DrillOptions
{
    /// <summary>The smallest interval the scheduler uses. A smaller configured value is raised to it.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(1);

    /// <summary>Time between drill runs. Weekly by default (doc 15, doc 48 section 11).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(7);
}
