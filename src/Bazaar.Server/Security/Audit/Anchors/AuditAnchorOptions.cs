namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Operator settings for external anchoring (design doc 48, section 5.3). Anchoring is off by default. When it is on, at least one
/// target must be enabled, and every enabled target must be configured. A setting may not loosen the floors and bounds below.
/// </summary>
public sealed record AuditAnchorOptions
{
    /// <summary>Shortest time one target may take to answer, in seconds.</summary>
    public static readonly TimeSpan MinTargetTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Longest time one target may take to answer, in seconds.</summary>
    public static readonly TimeSpan MaxTargetTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Whether checkpoints are anchored at all. Off until the operator configures a target.</summary>
    public bool Enabled { get; init; }

    /// <summary>Per-target time limit for one anchor or verify call. Must be between <see cref="MinTargetTimeout"/> and <see cref="MaxTargetTimeout"/>.</summary>
    public TimeSpan TargetTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The RFC 3161 time-stamp authority target.</summary>
    public Rfc3161TargetOptions Rfc3161 { get; init; } = new();

    /// <summary>The offsite Git target.</summary>
    public OffsiteGitTargetOptions OffsiteGit { get; init; } = new();

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a timeout outside its bounds, and <see cref="ArgumentException"/> when
    /// anchoring is enabled without an enabled target, or an enabled target is not configured.
    /// </summary>
    public void Validate()
    {
        if (TargetTimeout < MinTargetTimeout || TargetTimeout > MaxTargetTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetTimeout), TargetTimeout, $"Must be between {MinTargetTimeout} and {MaxTargetTimeout}.");
        }

        if (!Enabled)
        {
            return;
        }

        var enabledTargets = 0;
        if (Rfc3161.Enabled)
        {
            Rfc3161.Validate();
            enabledTargets++;
        }

        if (OffsiteGit.Enabled)
        {
            OffsiteGit.Validate();
            enabledTargets++;
        }

        if (enabledTargets == 0)
        {
            throw new ArgumentException("At least one anchor target must be enabled when anchoring is enabled.", nameof(Enabled));
        }
    }

    /// <summary>
    /// The enabled targets, in a fixed order. Empty when anchoring is off. Validates first, so an unconfigured target is refused here.
    /// </summary>
    public IReadOnlyList<IAuditAnchorTarget> CreateTargets(HttpClient httpClient, IGitRunner git, TimeProvider time)
    {
        Validate();
        if (!Enabled)
        {
            return [];
        }

        var targets = new List<IAuditAnchorTarget>();
        if (Rfc3161.Enabled)
        {
            targets.Add(new Rfc3161Target(httpClient, Rfc3161, time));
        }

        if (OffsiteGit.Enabled)
        {
            targets.Add(new OffsiteGitTarget(git, OffsiteGit, time));
        }

        return targets;
    }
}
