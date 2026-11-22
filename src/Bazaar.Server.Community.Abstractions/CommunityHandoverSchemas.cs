using System.Collections.Immutable;

namespace Bazaar.Server.Community;

/// <summary>Kind of a community hand-over field. There is deliberately no URL kind (doc 15: no URL type unless declared).</summary>
public enum CommunityFieldKind
{
    /// <summary>Free text bounded by <see cref="CommunityFieldSpec.MaxLength"/>.</summary>
    Text,

    /// <summary>One value from <see cref="CommunityFieldSpec.Values"/>.</summary>
    Enum,

    /// <summary>A bounded array of free text, each item bounded by <see cref="CommunityFieldSpec.MaxLength"/>.</summary>
    TextList,

    /// <summary>A bounded array of values from <see cref="CommunityFieldSpec.Values"/>.</summary>
    EnumList,

    /// <summary>A bounded array of GUID strings (lineage to the reader's source items).</summary>
    GuidList,

    /// <summary>A whole number from <see cref="CommunityFieldSpec.Min"/> to <see cref="CommunityFieldSpec.Max"/>.</summary>
    WholeNumber,

    /// <summary>A JSON boolean.</summary>
    Bool,
}

/// <summary>One field of a community hand-over schema, described as plain data.</summary>
/// <param name="Name">Field name: lowercase letters, digits and underscores.</param>
/// <param name="Kind">Field kind.</param>
/// <param name="Required">True when the payload must carry the field.</param>
/// <param name="MaxLength">Text length limit in code points (item limit for lists); 0 when unused.</param>
/// <param name="MaxCount">Array item limit; 0 when unused.</param>
/// <param name="Values">Allowed values for enum kinds.</param>
/// <param name="Min">Lower bound for integers.</param>
/// <param name="Max">Upper bound for integers.</param>
public sealed record CommunityFieldSpec(
    string Name,
    CommunityFieldKind Kind,
    bool Required = true,
    int MaxLength = 0,
    int MaxCount = 0,
    ImmutableArray<string> Values = default,
    long Min = 0,
    long Max = 0);

/// <summary>A hand-over schema described as plain data, so the host can map it onto the Server's schema registry.</summary>
/// <param name="Id">Schema id.</param>
/// <param name="Fields">Declared fields. Anything else in a payload is rejected.</param>
public sealed record CommunityHandoverSchema(string Id, ImmutableArray<CommunityFieldSpec> Fields);

/// <summary>
/// The community reader hand-over schema (doc 36, work package 11 [PROPOSED]). A reader session summarises watched
/// community content into this shape; the actor session sees only the validated fields. Free text is bounded and URL-like
/// text is refused, but bounded text can still carry injected wording, so no field is ever treated as an instruction.
/// </summary>
public static class CommunityHandoverSchemas
{
    /// <summary>Schema id a reader names in <c>submit_handover</c>.</summary>
    public const string ThreadSummaryId = "community_thread_summary";

    /// <summary>Longest summary, in code points.</summary>
    public const int SummaryMaxLength = 800;

    /// <summary>Largest source_refs array.</summary>
    public const int SourceRefsMaxCount = 20;

    /// <summary>Largest topic_tags array.</summary>
    public const int TopicTagsMaxCount = 5;

    /// <summary>Longest topic tag, in code points.</summary>
    public const int TopicTagMaxLength = 40;

    /// <summary>Longest target reference, in code points.</summary>
    public const int TargetRefMaxLength = 120;

    /// <summary>Allowed <c>platform</c> values.</summary>
    public static readonly ImmutableArray<string> Platforms = ["github", "forum", "discord", "reddit", "other"];

    /// <summary>Allowed <c>item_kind</c> values.</summary>
    public static readonly ImmutableArray<string> ItemKinds = ["issue", "pr", "thread", "comment"];

    /// <summary>Allowed <c>tone</c> values.</summary>
    public static readonly ImmutableArray<string> Tones = ["positive", "neutral", "negative", "mixed"];

    /// <summary>Allowed <c>flags</c> values: reader observations, never instructions.</summary>
    public static readonly ImmutableArray<string> Flags =
        ["off_topic", "hostile", "possible_duplicate", "rule_conflict", "contains_instructions_to_agent", "needs_human"];

    /// <summary>The <c>community_thread_summary</c> schema.</summary>
    public static CommunityHandoverSchema ThreadSummary { get; } = new(
        ThreadSummaryId,
        [
            new("platform", CommunityFieldKind.Enum, Values: Platforms),
            new("item_kind", CommunityFieldKind.Enum, Values: ItemKinds),
            new("target_ref", CommunityFieldKind.Text, MaxLength: TargetRefMaxLength),
            new("summary", CommunityFieldKind.Text, MaxLength: SummaryMaxLength),
            new("topic_tags", CommunityFieldKind.TextList, Required: false, MaxLength: TopicTagMaxLength, MaxCount: TopicTagsMaxCount),
            new("tone", CommunityFieldKind.Enum, Values: Tones),
            new("flags", CommunityFieldKind.EnumList, Required: false, MaxCount: Flags.Length, Values: Flags),
            new("reply_wanted", CommunityFieldKind.Bool),
            new("confidence", CommunityFieldKind.WholeNumber, Min: 0, Max: 100),
            new("source_refs", CommunityFieldKind.GuidList, MaxCount: SourceRefsMaxCount),
        ]);
}
