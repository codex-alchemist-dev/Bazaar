namespace Bazaar.GitHub.Ingress;

/// <summary>
/// Operator settings for the Events API poller (doc 21, Ingress and Settings). Bound from <c>Bazaar:GitHub:Ingress</c>.
/// The poller stays off until <see cref="InstallationId"/> and <see cref="Repositories"/> are set and an
/// <see cref="IGitHubEventSink"/> is registered.
/// </summary>
public sealed class GitHubIngressOptions
{
    /// <summary>The installation whose token reads the events. Zero means not configured.</summary>
    public long InstallationId { get; init; }

    /// <summary>Watched repositories as <c>owner/name</c>.</summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary><c>github.poll_interval_minutes</c> (doc 21 default 10). Never below 1. GitHub's <c>X-Poll-Interval</c> raises it, never lowers it.</summary>
    public int PollIntervalMinutes { get; init; } = 10;

    /// <summary>Agent App logins (for example <c>name-bazaar[bot]</c>); the mapper wakes and routes by these. Also treated as members.</summary>
    public IReadOnlyList<string> AgentLogins { get; init; } = [];

    /// <summary>
    /// Human logins (the owner, org members) whose events count as MEMBER, because the Events API does not state the actor's
    /// association. Everything else fails closed to external content (hard taint, no wake).
    /// </summary>
    public IReadOnlyList<string> MemberLogins { get; init; } = [];

    /// <summary>Events older than this are ignored, so a first start does not replay the 90-day window. Zero disables the cutoff. Default 24 hours.</summary>
    public TimeSpan MaxEventAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The principal id GitHub-bridged GC events are written under (<c>AuthorKind.ExternalBridge</c> or <c>System</c>). Required by <see cref="GitHubGcEventSink"/>.</summary>
    public Guid? BridgePrincipalId { get; init; }

    /// <summary>The install's Inbox GC (doc 21, Open question 2), where external issue and PR events land. Null means they are not posted.</summary>
    public Guid? InboxGcId { get; init; }

    /// <summary>First wait after a failed poll; doubles per consecutive failure up to <see cref="MaxFailureBackoff"/>.</summary>
    public TimeSpan FailureBackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxFailureBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Shortest sleep of the service loop, so the loop can never spin.</summary>
    public TimeSpan MinLoopDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Bound on remembered per-PR applied state.</summary>
    public int MaxTrackedPullRequests { get; init; } = 10_000;

    /// <summary>The parsed <c>owner/name</c> pairs; entries that are not exactly two non-empty parts are dropped.</summary>
    public IReadOnlyList<(string Owner, string Repo)> ParsedRepositories() =>
        [.. Repositories
            .Select(r => r.Split('/', StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => (p[0], p[1]))
            .Distinct()];

    /// <summary>Why the poller must not start, or null when it may.</summary>
    public string? DisabledReason() =>
        InstallationId <= 0 ? "no installation id is configured (Bazaar:GitHub:Ingress:InstallationId)"
        : ParsedRepositories().Count == 0 ? "no repositories are configured (Bazaar:GitHub:Ingress:Repositories, owner/name)"
        : null;
}
