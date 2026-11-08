using System.Collections.Frozen;

namespace Bazaar.Server.Agents.Inbox;

/// <summary>The inbox item kinds that can wake an agent (doc 06, "Wake model").</summary>
public static class InboxKind
{
    /// <summary>Pushed at the next tool boundary (doc 24 stack).</summary>
    public const string Interrupt = "interrupt";

    /// <summary>A mention or a direct message (doc 01).</summary>
    public const string Mention = "mention";

    /// <summary>A card is assigned to the agent, or review is requested (doc 12).</summary>
    public const string Assignment = "assignment";

    /// <summary>A Council ballot is requested (doc 10).</summary>
    public const string VoteRequest = "vote_request";

    /// <summary>A shift start or a reminder (doc 09).</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Lucky mode start (doc 26).</summary>
    public const string Brainstorm = "brainstorm";

    /// <summary>A digest item the Skimmer moved to wake-now (doc 13).</summary>
    public const string SkimmerPromoted = "skimmer_promoted";

    /// <summary>Batched unmentioned GC activity (doc 06, digest).</summary>
    public const string GcDigest = "gc_digest";

    /// <summary>A Constitution change (doc 24, Propagation). Digest-class: it never wakes an agent.</summary>
    public const string Constitution = "constitution";

    /// <summary>A RyuseiFuda one-shot timer fired (doc 38, Wake reasons). Keyed by the fire id; ranked with scheduled wakes.</summary>
    public const string RyuseiTimer = "ryusei_timer";

    /// <summary>A RyuseiFuda interval fired (doc 38, Wake reasons). Keyed by the fire id; ranked with scheduled wakes.</summary>
    public const string RyuseiInterval = "ryusei_interval";

    /// <summary>A RyuseiFuda watcher's predicate held (doc 38, Wake reasons). Keyed by the fire id; ranked with scheduled wakes.</summary>
    public const string RyuseiWatcher = "ryusei_watcher";

    /// <summary>A RyuseiFuda event trigger matched (doc 38, Wake reasons). Keyed by the fire id; ranked with scheduled wakes.</summary>
    public const string RyuseiEvent = "ryusei_event";

    /// <summary>Every known kind.</summary>
    public static IReadOnlySet<string> All { get; } = new[]
    {
        Interrupt, Mention, Assignment, VoteRequest, Scheduled, Brainstorm, SkimmerPromoted, GcDigest, Constitution,
        RyuseiTimer, RyuseiInterval, RyuseiWatcher, RyuseiEvent,
    }.ToFrozenSet(StringComparer.Ordinal);
}
