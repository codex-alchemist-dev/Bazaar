namespace Bazaar.Server.Fun.Mirror;

/// <summary><c>mirror.model_source</c> values (doc 26 §5, Settings table).</summary>
public enum MirrorModelSource
{
    /// <summary>Local model or server-side heuristics (default).</summary>
    Local,

    /// <summary>Hosted model approved by operator.</summary>
    OperatorApprovedHosted,
}

/// <summary>
/// Settings for the Mirror Agent (docs/design/26-fun-systems.md §5, <c>mirror.*</c>).
/// Defaults match the doc 26 settings table.
/// </summary>
public sealed class MirrorSettings
{
    /// <summary>Well-known signal kinds accepted by the Mirror Agent.</summary>
    public static readonly IReadOnlyList<string> DefaultSignalKinds =
    [
        "accept/reject",
        "owner edits",
        "review comments",
        "Council tie-breaks",
        "explicit statements",
    ];

    /// <summary><c>mirror.signal_kinds</c>: kinds of owner decisions that the Mirror learns from.</summary>
    public IReadOnlyList<string> SignalKinds { get; init; } = DefaultSignalKinds;

    /// <summary><c>mirror.evidence_threshold</c>: number of observations before a preference rule applies (default 5).</summary>
    public int EvidenceThreshold { get; init; } = 5;

    /// <summary><c>mirror.allow_rewrite_offers</c>: whether to suggest optional rewrites (default true).</summary>
    public bool AllowRewriteOffers { get; init; } = true;

    /// <summary><c>mirror.model_source</c>: model execution locality (default Local).</summary>
    public MirrorModelSource ModelSource { get; init; } = MirrorModelSource.Local;

    /// <summary>Whether learning from new owner decisions is paused.</summary>
    public bool LearningPaused { get; set; }

    /// <summary>Validates setting constraints.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(EvidenceThreshold, 1);
        if (SignalKinds == null || SignalKinds.Count == 0)
        {
            throw new ArgumentException("SignalKinds must not be empty.", nameof(SignalKinds));
        }
    }
}
