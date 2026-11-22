namespace Bazaar.Server.Council.Prompts;

/// <summary>Outcome of <see cref="PrivacyGuard"/>: allowed, or denied with a reason.</summary>
public abstract record PrivacyCheckResult
{
    /// <summary>The prompt may be sent.</summary>
    public sealed record Allowed : PrivacyCheckResult;

    /// <summary>The prompt must not be sent.</summary>
    /// <param name="Reason">Why it was denied.</param>
    public sealed record Denied(string Reason) : PrivacyCheckResult;
}
