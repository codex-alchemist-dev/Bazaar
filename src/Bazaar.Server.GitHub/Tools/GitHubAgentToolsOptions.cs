using Bazaar.Server.RateLimits;

namespace Bazaar.Server.GitHub.Tools;

/// <summary>
/// Operator settings for the agent GitHub tools (doc 21, Agent tools [PROPOSED]). <see cref="WritesPerHour"/> is bound from
/// <c>Bazaar:GitHub:WritesPerHour</c> and is the per-installation writes limit (<c>github.writes_per_hour</c>).
/// </summary>
public sealed class GitHubAgentToolsOptions
{
    /// <summary>Doc 21 default for <c>github.writes_per_hour</c>.</summary>
    public const int DefaultWritesPerHour = 300;

    /// <summary>Upper bound that catches a typo; a larger value is a composition error.</summary>
    public const int MaxWritesPerHour = 100_000;

    /// <summary>Most agent write calls admitted per GitHub App installation in a sliding hour.</summary>
    public int WritesPerHour { get; init; } = DefaultWritesPerHour;

    /// <summary>The quota the write tools count against, keyed by installation id.</summary>
    public RateLimitPolicy WritesPerHourPolicy => new("github_writes_per_hour", WritesPerHour, TimeSpan.FromHours(1), RateLimitScope.Installation, "github.writes_per_hour");

    /// <summary>Throws when the value is outside 1..<see cref="MaxWritesPerHour"/>. Called at composition, so a bad setting stops startup.</summary>
    public GitHubAgentToolsOptions Validate()
    {
        if (WritesPerHour is < 1 or > MaxWritesPerHour)
        {
            throw new InvalidOperationException($"Bazaar:GitHub:WritesPerHour must be between 1 and {MaxWritesPerHour}, not {WritesPerHour}.");
        }

        return this;
    }
}
