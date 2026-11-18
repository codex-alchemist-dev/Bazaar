namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Result of <see cref="KillSwitchEngine.Evaluate"/>. A call is either allowed or denied with the scope that denied it and
/// a short human-readable reason.
/// </summary>
/// <param name="Allowed">True when the call may proceed.</param>
/// <param name="Scope">The switch that denied the call. Null when allowed.</param>
/// <param name="Reason">Why the call was denied. Null when allowed.</param>
public sealed record KillSwitchDecision(bool Allowed, KillSwitchScope? Scope, string? Reason)
{
    /// <summary>A decision that lets the call proceed.</summary>
    public static KillSwitchDecision Allow { get; } = new(true, null, null);

    /// <summary>A decision that denies the call at the given scope.</summary>
    /// <param name="scope">The switch that denied the call.</param>
    /// <param name="reason">Why the call was denied. Must not be blank.</param>
    public static KillSwitchDecision Deny(KillSwitchScope scope, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new KillSwitchDecision(false, scope, reason);
    }
}
