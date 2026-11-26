namespace Bazaar.Server.Knowledge.Health;

/// <summary>Which health rule raised a reason.</summary>
public enum HealthReasonKind
{
    CoverageDrop,
    BuildFailureStreak,
}

/// <summary>One reason a project is flagged. <see cref="Message"/> is for people; the kind is for code.</summary>
public sealed record HealthReason(HealthReasonKind Kind, string Message);

/// <summary>Health status of one project for the project panel.</summary>
public enum HealthStatus
{
    /// <summary>Data exists and no rule fired.</summary>
    Healthy,

    /// <summary>At least one rule fired; see the reasons.</summary>
    Alert,

    /// <summary>No coverage or CI samples fall in the window, so nothing can be judged.</summary>
    NoData,
}

/// <summary>The project panel's health block.</summary>
public sealed record ProjectHealthPanel(string ProjectId, DateOnly AsOf, HealthStatus Status, IReadOnlyList<HealthReason> Reasons);
