namespace Bazaar.Server.Mirror;

/// <summary>The owner decisions Mirror may learn from (doc 26 section 5, <c>mirror.signal_kinds</c>). Nothing else is ever captured.</summary>
public enum MirrorSignalKind
{
    CardAccepted = 0,
    CardRejected = 1,
    OwnerEdit = 2,
    ReviewComment = 3,
    CouncilTieBreak = 4,
    PreferenceStatement = 5,
}

/// <summary>Whether an observation says the owner likes a topic or dislikes it.</summary>
public enum MirrorDirection
{
    Prefer = 0,
    Avoid = 1,
}

/// <summary>Where a model that touches the profile may run (<c>mirror.model_source</c>).</summary>
public enum MirrorModelSource
{
    Local = 0,
    OperatorApprovedHosted = 1,
}

/// <summary>One observed owner decision. <see cref="Payload"/> is free text (the reason, the edit) and is stored encrypted.</summary>
/// <param name="Topic">What the preference is about, for example <c>tone:formal</c> or <c>scope:small-prs</c>.</param>
/// <param name="SourceRef">The card, edit, review or vote this came from, so a rule can show its evidence.</param>
public sealed record MirrorSignal(
    Guid Id,
    MirrorSignalKind Kind,
    string Topic,
    MirrorDirection Direction,
    string SourceRef,
    double Weight,
    string Payload,
    DateTimeOffset CreatedAt);

/// <summary>A learned preference. It applies only once <see cref="Count"/> reaches the evidence threshold, and shows that count.</summary>
public sealed record MirrorRule(string Topic, MirrorDirection Direction, int Count, IReadOnlyList<string> EvidenceRefs);

/// <summary>One immutable version of the owner profile.</summary>
public sealed record MirrorProfile(int Version, IReadOnlyList<MirrorRule> Rules, DateTimeOffset CreatedAt);

/// <summary>An agent suggestion about to be shown to the owner, with the topics it touches.</summary>
public sealed record MirrorItem(string Id, string Text, IReadOnlyList<string> Topics);

/// <summary>A note beside an item, naming the rule and its evidence count.</summary>
public sealed record MirrorAnnotation(string Topic, MirrorDirection Direction, int Count, string Note);

/// <summary>A rewrite offered as a separate suggestion. The original item is never replaced.</summary>
public sealed record MirrorRewriteOffer(string ItemId, string Text);

/// <summary>An item as shown to the owner: always the original, plus annotations, a rank and maybe a rewrite offer.</summary>
public sealed record MirrorAnnotatedItem(
    MirrorItem Original,
    IReadOnlyList<MirrorAnnotation> Annotations,
    double Score,
    MirrorRewriteOffer? RewriteOffer);

/// <summary>Learning controls the owner holds (doc 26 section 5 owner controls).</summary>
public interface IMirrorService
{
    /// <summary>Records a signal and rebuilds the profile. Returns false when learning is paused or the kind is not enabled.</summary>
    Task<bool> RecordAsync(MirrorSignal signal, CancellationToken cancellationToken);

    /// <summary>The newest profile version, or null before any exists.</summary>
    Task<MirrorProfile?> GetProfileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Annotates and orders suggestions for the owner's view. The result has exactly one entry per input item:
    /// Mirror never removes an item, and equal scores keep the input order.
    /// </summary>
    Task<IReadOnlyList<MirrorAnnotatedItem>> AnnotateAsync(IReadOnlyList<MirrorItem> items, CancellationToken cancellationToken);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);

    /// <summary>Deletes one rule (its signals stay out of the next rebuild). Writes a new profile version.</summary>
    Task DeleteRuleAsync(string topic, MirrorDirection direction, CancellationToken cancellationToken);

    /// <summary>Removes every signal, profile version and any derived index.</summary>
    Task WipeAsync(CancellationToken cancellationToken);
}

/// <summary>Storage for signals. The Postgres store encrypts <see cref="MirrorSignal.Payload"/> per person (doc 26, C01).</summary>
public interface IMirrorStore
{
    Task AddSignalAsync(MirrorSignal signal, CancellationToken cancellationToken);
    Task<IReadOnlyList<MirrorSignal>> ListSignalsAsync(CancellationToken cancellationToken);
    Task AddProfileAsync(MirrorProfile profile, CancellationToken cancellationToken);
    Task<MirrorProfile?> GetLatestProfileAsync(CancellationToken cancellationToken);
    Task<bool> GetPausedAsync(CancellationToken cancellationToken);
    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> ListSuppressedAsync(CancellationToken cancellationToken);
    Task AddSuppressedAsync(string key, CancellationToken cancellationToken);
    Task WipeAsync(CancellationToken cancellationToken);
}

/// <summary>Optional rewriter. A hosted one is refused unless the operator approved hosted models for Mirror.</summary>
public interface IMirrorRewriter
{
    bool IsHosted { get; }
    Task<string?> RewriteAsync(MirrorItem item, IReadOnlyList<MirrorRule> applicableRules, CancellationToken cancellationToken);
}
