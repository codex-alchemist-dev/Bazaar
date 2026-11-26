namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// A threshold rule's settings: fire when <see cref="Threshold"/> events by one agent (per key) fall inside one <see cref="Window"/>.
/// </summary>
/// <param name="Threshold">How many events inside the window make a finding.</param>
/// <param name="Window">The sliding window the events must fall in.</param>
/// <param name="Severity">The severity of the alert.</param>
public sealed record WindowedRuleOptions(int Threshold, TimeSpan Window, AnomalySeverity Severity)
{
    /// <summary>The largest threshold accepted. A rule keeps up to this many event times per key.</summary>
    public const int MaxThreshold = 1000;

    /// <summary>The shortest window accepted.</summary>
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);

    /// <summary>The longest window accepted.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

    internal void Validate(string name)
    {
        if (Threshold is < 1 or > MaxThreshold)
        {
            throw new ArgumentOutOfRangeException($"{name}.{nameof(Threshold)}", Threshold, $"The threshold must be from 1 to {MaxThreshold}.");
        }

        if (Window < MinWindow || Window > MaxWindow)
        {
            throw new ArgumentOutOfRangeException($"{name}.{nameof(Window)}", Window, $"The window must be from {MinWindow} to {MaxWindow}.");
        }

        if (!Enum.IsDefined(Severity))
        {
            throw new ArgumentOutOfRangeException($"{name}.{nameof(Severity)}", Severity, "The severity is not a known value.");
        }
    }
}

/// <summary>A rule that fires on each matching event and has only a severity to set.</summary>
/// <param name="Severity">The severity of the alert.</param>
public sealed record SeverityRuleOptions(AnomalySeverity Severity)
{
    internal void Validate(string name)
    {
        if (!Enum.IsDefined(Severity))
        {
            throw new ArgumentOutOfRangeException($"{name}.{nameof(Severity)}", Severity, "The severity is not a known value.");
        }
    }
}

/// <summary>
/// The settings of the four shipped anomaly rules. The defaults are safe drafts (doc 48 section 5.6 leaves the values open); an
/// operator overrides them through the registration. <see cref="Validate"/> rejects values outside the accepted range.
/// </summary>
public sealed record AnomalyRuleOptions
{
    /// <summary>Object reads of one object class by one agent, within a window.</summary>
    public WindowedRuleOptions ReadBurst { get; init; } = new(50, TimeSpan.FromMinutes(5), AnomalySeverity.High);

    /// <summary>Scoped accesses outside the agent's scope.</summary>
    public SeverityRuleOptions OutOfScopeRead { get; init; } = new(AnomalySeverity.High);

    /// <summary>Canary touches.</summary>
    public SeverityRuleOptions CanaryHit { get; init; } = new(AnomalySeverity.Critical);

    /// <summary>Approval requests by one agent, within a window.</summary>
    public WindowedRuleOptions ApprovalFlood { get; init; } = new(10, TimeSpan.FromMinutes(10), AnomalySeverity.Medium);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside the accepted range. Returns this when valid.</summary>
    public AnomalyRuleOptions Validate()
    {
        ReadBurst.Validate(nameof(ReadBurst));
        OutOfScopeRead.Validate(nameof(OutOfScopeRead));
        CanaryHit.Validate(nameof(CanaryHit));
        ApprovalFlood.Validate(nameof(ApprovalFlood));
        return this;
    }
}
