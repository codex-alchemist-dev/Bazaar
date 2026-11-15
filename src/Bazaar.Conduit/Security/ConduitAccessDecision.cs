namespace Bazaar.Conduit.Security;

/// <summary>Whether a Conduit listener admits a request or records an attempt as allowed.</summary>
public enum ConduitAccessVerdict
{
    /// <summary>The request may proceed.</summary>
    Allowed,

    /// <summary>The request is refused. <see cref="ConduitAccessDecision.Reason"/> says why.</summary>
    Denied,
}

/// <summary>Why a Conduit listener refused a request.</summary>
public enum ConduitDenyReason
{
    /// <summary>No denial.</summary>
    None,

    /// <summary>The client exceeded the sliding-window request rate.</summary>
    RateLimited,

    /// <summary>The client is locked out after failed sign-ins and the lockout has not ended.</summary>
    LockedOut,
}

/// <summary>
/// Result of <see cref="ConduitAccessGuard.TryAdmitRequest"/> or <see cref="ConduitAccessGuard.RecordAuthResult"/>.
/// <see cref="RetryAfter"/> is the time until the client may try again; it is zero when the request is allowed.
/// </summary>
/// <param name="Verdict">Allowed or denied.</param>
/// <param name="Reason">Why it was denied, or <see cref="ConduitDenyReason.None"/>.</param>
/// <param name="RetryAfter">Time until a retry can succeed.</param>
public readonly record struct ConduitAccessDecision(ConduitAccessVerdict Verdict, ConduitDenyReason Reason, TimeSpan RetryAfter)
{
    /// <summary>An allowed decision with no wait.</summary>
    public static ConduitAccessDecision Allowed { get; } = new(ConduitAccessVerdict.Allowed, ConduitDenyReason.None, TimeSpan.Zero);

    /// <summary>A denied decision with the given reason and wait.</summary>
    public static ConduitAccessDecision Denied(ConduitDenyReason reason, TimeSpan retryAfter) =>
        new(ConduitAccessVerdict.Denied, reason, retryAfter);

    /// <summary>True when <see cref="Verdict"/> is <see cref="ConduitAccessVerdict.Allowed"/>.</summary>
    public bool IsAllowed => Verdict == ConduitAccessVerdict.Allowed;
}
