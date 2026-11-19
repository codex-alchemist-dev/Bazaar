namespace Bazaar.Conduit.Security;

/// <summary>
/// Shipped ceilings for Conduit listener limits. Doc 49 section 5 and doc 32 section 7. These are floors, not settings:
/// an operator may tighten a limit below them, never loosen it past them. A Conduit can never raise them.
/// </summary>
public static class ConduitAccessFloor
{
    /// <summary>Highest admitted request rate per listener and client, in requests per second (doc 32 section 7, PROPOSED).</summary>
    public const double MaxRequestsPerSecond = 20;

    /// <summary>Most failed sign-ins allowed inside the failure window before a lockout (doc 49 section 5).</summary>
    public const int MaxFailureThreshold = 10;

    /// <summary>Longest lockout the backoff may reach (doc 49 section 5).</summary>
    public static readonly TimeSpan MaxLockoutCap = TimeSpan.FromHours(24);
}

/// <summary>
/// Request rate and failed-attempt lockout policy for one Conduit listener. Doc 49 section 5 (lockout values and floors),
/// doc 32 section 7 (request rate starting value). Defaults are PLANNED, operator-configurable values; the floors in
/// <see cref="ConduitAccessFloor"/> are not.
/// </summary>
public sealed record ConduitAccessOptions
{
    /// <summary>Requests admitted per client within one <see cref="RequestWindow"/> (sliding window).</summary>
    public int RequestLimit { get; init; } = 20;

    /// <summary>Length of the sliding request window.</summary>
    public TimeSpan RequestWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Failed sign-ins inside <see cref="FailureWindow"/> that start a lockout.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Window in which failed sign-ins are counted.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Length of the first lockout. Each repeat lockout doubles it.</summary>
    public TimeSpan BaseLockout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Longest lockout the doubling backoff may reach.</summary>
    public TimeSpan MaxLockout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The shipped defaults.</summary>
    public static ConduitAccessOptions Shipped { get; } = new();

    /// <summary>
    /// Returns the reasons these options are invalid on their own: a value that is zero or negative, or a base lockout
    /// longer than the maximum. An empty list means valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (RequestLimit <= 0)
        {
            problems.Add("RequestLimit must be positive.");
        }

        if (RequestWindow <= TimeSpan.Zero)
        {
            problems.Add("RequestWindow must be positive.");
        }

        if (FailureThreshold <= 0)
        {
            problems.Add("FailureThreshold must be positive.");
        }

        if (FailureWindow <= TimeSpan.Zero)
        {
            problems.Add("FailureWindow must be positive.");
        }

        if (BaseLockout <= TimeSpan.Zero)
        {
            problems.Add("BaseLockout must be positive.");
        }

        if (MaxLockout <= TimeSpan.Zero)
        {
            problems.Add("MaxLockout must be positive.");
        }

        if (BaseLockout > MaxLockout)
        {
            problems.Add("BaseLockout must not exceed MaxLockout.");
        }

        return problems;
    }

    /// <summary>
    /// Returns the reasons these options are looser than the shipped floor: a request rate above
    /// <see cref="ConduitAccessFloor.MaxRequestsPerSecond"/>, a threshold above <see cref="ConduitAccessFloor.MaxFailureThreshold"/>,
    /// or a maximum lockout above <see cref="ConduitAccessFloor.MaxLockoutCap"/>. An empty list means within the floor.
    /// Call <see cref="Validate"/> first; invalid values are reported there and skipped here.
    /// </summary>
    public IReadOnlyList<string> ValidateAgainstFloor()
    {
        var problems = new List<string>();
        if (RequestLimit > 0 && RequestWindow > TimeSpan.Zero &&
            RequestLimit / RequestWindow.TotalSeconds > ConduitAccessFloor.MaxRequestsPerSecond)
        {
            problems.Add($"Request rate may not exceed {ConduitAccessFloor.MaxRequestsPerSecond} per second.");
        }

        if (FailureThreshold > ConduitAccessFloor.MaxFailureThreshold)
        {
            problems.Add($"FailureThreshold may not exceed {ConduitAccessFloor.MaxFailureThreshold}.");
        }

        if (MaxLockout > ConduitAccessFloor.MaxLockoutCap)
        {
            problems.Add($"MaxLockout may not exceed {ConduitAccessFloor.MaxLockoutCap}.");
        }

        return problems;
    }
}
