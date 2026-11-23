namespace Bazaar.Kodo.Chaos;

/// <summary>
/// Chaos gate settings (doc 46 sections 17.3 and 17.7). Off by default. The limits may be tightened by the operator but never loosened
/// past the hard ceilings, so <see cref="Validate"/> reports a value above a ceiling as invalid and the gate then refuses everything.
/// </summary>
public sealed record ChaosSettings
{
    /// <summary>The permission a person needs to enable chaos (section 17.3).</summary>
    public const string RequiredPermission = "kodo.chaos.enable";

    /// <summary>No experiment may run longer than this, whatever the settings say.</summary>
    public static readonly TimeSpan CeilingExperimentDuration = TimeSpan.FromHours(1);

    /// <summary>No plan may run longer in total than this, whatever the settings say.</summary>
    public static readonly TimeSpan CeilingTotalDuration = TimeSpan.FromHours(4);

    /// <summary>The blast radius ceiling, in percent of the sandbox.</summary>
    public const int CeilingBlastRadiusPercent = 50;

    /// <summary>The abort must stop an experiment within this (section 17.3).</summary>
    public static readonly TimeSpan CeilingAbortWithin = TimeSpan.FromSeconds(10);

    /// <summary>The most experiments one plan may hold.</summary>
    public const int CeilingExperiments = 50;

    /// <summary>Whether chaos mode is on at all. Default off.</summary>
    public bool Enabled { get; init; }

    /// <summary>The longest one experiment may run. Default 15 minutes.</summary>
    public TimeSpan MaxExperimentDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest a whole plan may run. Default 1 hour.</summary>
    public TimeSpan MaxTotalDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The widest blast radius an experiment may declare, in percent. Default 25.</summary>
    public int MaxBlastRadiusPercent { get; init; } = 25;

    /// <summary>The most experiments in one plan. Default 10.</summary>
    public int MaxExperiments { get; init; } = 10;

    /// <summary>How old the step-up proof may be. Default 5 minutes, at most 15.</summary>
    public TimeSpan MaxStepUpAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Identifiers that are production targets. A target equal to one of these is refused even if it looks like a sandbox.</summary>
    public IReadOnlySet<string> KnownProductionIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when every limit is inside its ceiling and floor.</summary>
    public bool IsValid() =>
        MaxExperimentDuration > TimeSpan.Zero && MaxExperimentDuration <= CeilingExperimentDuration
        && MaxTotalDuration >= MaxExperimentDuration && MaxTotalDuration <= CeilingTotalDuration
        && MaxBlastRadiusPercent is >= 1 and <= CeilingBlastRadiusPercent
        && MaxExperiments is >= 1 and <= CeilingExperiments
        && MaxStepUpAge > TimeSpan.Zero && MaxStepUpAge <= TimeSpan.FromMinutes(15)
        && KnownProductionIds is not null;
}
