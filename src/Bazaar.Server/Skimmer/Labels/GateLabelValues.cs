namespace Bazaar.Server.Skimmer.Labels;

/// <summary>
/// Label and source values for <c>gate_labels</c> (doc 13, "Measurement" and "Feedback"). The source values mirror the
/// CHECK constraint in migration 0016. The flag labels are the closed set an agent flag may write.
/// </summary>
public static class GateLabelValues
{
    /// <summary>Should have woken, was deferred, and was attended after 60 minutes or never (a miss).</summary>
    public const string Missed = "missed";

    /// <summary>Should have deferred, was woken (a false wake).</summary>
    public const string FalseWake = "false_wake";

    /// <summary>Source for labels written by an agent flag tool.</summary>
    public const string SourceAgentFlag = "agent_flag";

    /// <summary>Source for labels written during owner review of the offline set.</summary>
    public const string SourceOwnerReview = "owner_review";

    /// <summary>Source for labels derived from outcomes (the agent replied to or acted on the message in the window).</summary>
    public const string SourceOutcome = "outcome";

    /// <summary>The labels an agent flag may write.</summary>
    public static IReadOnlySet<string> FlagLabels { get; } = new HashSet<string>(StringComparer.Ordinal) { Missed, FalseWake };
}
