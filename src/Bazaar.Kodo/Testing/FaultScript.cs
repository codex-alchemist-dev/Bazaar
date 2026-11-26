namespace Bazaar.Kodo.Testing;

/// <summary>The faults the harness can script (doc 46 section 17.1, PROPOSED subset, see 17.5).</summary>
public enum FaultKind
{
    /// <summary>A probe reports red for a while.</summary>
    ProbeFailure,

    /// <summary>The clock jumps forward or backward by an offset and stays there.</summary>
    ClockJump,

    /// <summary>Kodo exits and restarts several times in a row (section 7.1).</summary>
    RestartStorm,

    /// <summary>A critical reading stops being refreshed, so it is stale and therefore unknown (section 9).</summary>
    StaleData,
}

/// <summary>
/// One scripted fault. <see cref="At"/> is on the scenario's own timeline, which a clock jump does not move, so a script reads the
/// same however the clock is skewed.
/// </summary>
public sealed record ScriptedFault
{
    private ScriptedFault()
    {
    }

    public FaultKind Kind { get; private init; }

    /// <summary>When the fault starts, counted from the start of the run.</summary>
    public TimeSpan At { get; private init; }

    /// <summary>How long a probe failure or stale reading lasts. Zero for the other kinds.</summary>
    public TimeSpan Duration { get; private init; }

    /// <summary>The clock offset of a <see cref="FaultKind.ClockJump"/>. Negative moves the clock back.</summary>
    public TimeSpan ClockOffset { get; private init; }

    /// <summary>For a probe failure: true for a critical probe, false for a non-critical one.</summary>
    public bool Critical { get; private init; }

    /// <summary>The number of exits in a <see cref="FaultKind.RestartStorm"/>.</summary>
    public int Restarts { get; private init; }

    public static ScriptedFault ProbeFailure(TimeSpan at, TimeSpan duration, bool critical = true)
    {
        Require(at, duration);
        return new ScriptedFault { Kind = FaultKind.ProbeFailure, At = at, Duration = duration, Critical = critical };
    }

    public static ScriptedFault ClockJump(TimeSpan at, TimeSpan offset)
    {
        Require(at, TimeSpan.Zero);
        return new ScriptedFault { Kind = FaultKind.ClockJump, At = at, ClockOffset = offset };
    }

    public static ScriptedFault RestartStorm(TimeSpan at, int restarts)
    {
        Require(at, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(restarts, 1);
        return new ScriptedFault { Kind = FaultKind.RestartStorm, At = at, Restarts = restarts };
    }

    public static ScriptedFault StaleData(TimeSpan at, TimeSpan duration)
    {
        Require(at, duration);
        return new ScriptedFault { Kind = FaultKind.StaleData, At = at, Duration = duration };
    }

    private static void Require(TimeSpan at, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(at, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
    }
}

/// <summary>An ordered list of faults. Order of equal times is the order given.</summary>
public sealed class FaultScript
{
    public FaultScript(IEnumerable<ScriptedFault> faults)
    {
        ArgumentNullException.ThrowIfNull(faults);
        Faults = faults.Select((fault, index) => (fault, index))
            .OrderBy(pair => pair.fault.At)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.fault)
            .ToArray();
    }

    public IReadOnlyList<ScriptedFault> Faults { get; }
}
