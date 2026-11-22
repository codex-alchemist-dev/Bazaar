namespace Bazaar.Kodo.Incidents;

/// <summary>
/// Incident mode settings (doc 46 sections 16.3 and 20). Every default is the conservative one and is PROPOSED where the doc is silent
/// (section 16.9). Settings may tighten, never loosen below the floor: the automatic-entry grace has a ceiling and exit is never automatic.
/// </summary>
public sealed record IncidentOptions
{
    /// <summary>The ceiling of <see cref="CriticalRedGrace"/>: the doc's default of 10 minutes is the loosest allowed.</summary>
    public static TimeSpan CriticalRedGraceCeiling { get; } = TimeSpan.FromMinutes(10);

    /// <summary>A critical probe red for this long opens an incident (section 16.3). Default and ceiling 10 minutes, floor 1 minute.</summary>
    public TimeSpan CriticalRedGrace { get; init; } = CriticalRedGraceCeiling;

    /// <summary>The probes must be green for this long before an incident may be resolved (section 8.2). Default 5 minutes.</summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether manual entry also needs step-up. Default false: section 16.3 asks for step-up on exit only.</summary>
    public bool EnterRequiresStepUp { get; init; }

    /// <summary>Config keys that may still change while frozen (the incident allowlist, section 16.3 item 2). Empty by default.</summary>
    public IReadOnlySet<string> ConfigAllowlist { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Operator additions: a cause (probe id) to the ordered runbook ids that help. Merged after the shipped mapping.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CauseRunbooks { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>How far before the opening time the journal summary looks (section 16.4). Default 1 hour.</summary>
    public TimeSpan JournalLookBack { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Checks every value. A bad value throws, so a bad setting cannot silently loosen the freeze.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CriticalRedGrace, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CriticalRedGrace, CriticalRedGraceCeiling);
        ArgumentOutOfRangeException.ThrowIfLessThan(StabilityWindow, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StabilityWindow, TimeSpan.FromHours(24));
        ArgumentOutOfRangeException.ThrowIfLessThan(JournalLookBack, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(ConfigAllowlist);
        ArgumentNullException.ThrowIfNull(CauseRunbooks);
    }
}

/// <summary>The inputs of the automatic-entry rule (section 16.3 Entry).</summary>
/// <param name="CriticalRedSince">When a critical probe turned red and stayed red, or null.</param>
/// <param name="IntegrityFailure">An integrity failure is open (section 6.4).</param>
/// <param name="SplitBrain">A split-brain signal (section 7.6).</param>
/// <param name="ReplicationBreak">A configured standby's replication is broken.</param>
public sealed record IncidentSignals(DateTimeOffset? CriticalRedSince, bool IntegrityFailure, bool SplitBrain, bool ReplicationBreak);

/// <summary>The automatic entry floor of section 16.3: these four triggers always open an incident. A setting can only tighten the grace.</summary>
public static class IncidentAutoEntry
{
    /// <summary>The trigger to open an incident for, or null. Integrity, split-brain and replication open at once; a red probe waits out its grace.</summary>
    public static IncidentTrigger? Evaluate(IncidentSignals signals, DateTimeOffset now, IncidentOptions options)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (signals.IntegrityFailure)
        {
            return IncidentTrigger.IntegrityFailure;
        }

        if (signals.SplitBrain)
        {
            return IncidentTrigger.SplitBrain;
        }

        if (signals.ReplicationBreak)
        {
            return IncidentTrigger.ReplicationBreak;
        }

        return signals.CriticalRedSince is { } since && now - since >= options.CriticalRedGrace
            ? IncidentTrigger.CriticalProbeRed
            : null;
    }
}
