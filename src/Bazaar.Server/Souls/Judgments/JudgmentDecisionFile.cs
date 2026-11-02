using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Judgments;

/// <summary>
/// Builds the Markdown decision file for a judgment (docs/design/07-soul-system.md, "Auditable judgments"). Front matter
/// carries the identifiers and the Soul version; the body carries the options, the choice, the reasoning and the logged
/// provenance of each input.
/// </summary>
public static partial class JudgmentDecisionFile
{
    private const int _maxSlugLength = 60;
    private const string _fallbackSlug = "decision";

    /// <summary>Builds the file for a validated record. Call <see cref="JudgmentWriter"/> rather than this directly.</summary>
    public static DecisionFile Build(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var title = record.Title.Trim();
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("decision_id: ").Append(Quote(record.DecisionId.ToString("D"))).Append('\n');
        builder.Append("soul_id: ").Append(Quote(record.SoulId.ToString("D"))).Append('\n');
        builder.Append("soul_version: ").Append(record.SoulVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("gc_id: ").Append(Quote(record.GcId.ToString("D"))).Append('\n');
        builder.Append("agent_id: ").Append(Quote(record.AgentId.ToString("D"))).Append('\n');
        builder.Append("type: decision\n");
        builder.Append("kind: ").Append(Quote(KindName(record.Kind))).Append('\n');
        builder.Append("title: ").Append(Quote(title)).Append('\n');
        builder.Append("confidence: ").Append(Quote(ConfidenceName(record.Confidence))).Append('\n');
        builder.Append("decided_at: ").Append(Quote(record.DecidedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append('\n');
        builder.Append("---\n\n");

        builder.Append("# ").Append(title).Append("\n\n");

        builder.Append("## Options\n\n");
        foreach (var option in record.Options)
            builder.Append("- ").Append(option.Trim()).Append('\n');
        builder.Append('\n');

        builder.Append("## Chosen\n\n").Append(record.Chosen.Trim()).Append("\n\n");
        builder.Append("## Reasoning\n\n").Append(record.Reasoning.Trim()).Append("\n\n");

        builder.Append("## Inputs\n\n");
        if (record.Inputs.Count == 0)
            builder.Append("None recorded.\n");
        foreach (var input in record.Inputs)
            builder.Append("- seq ").Append(input.Seq.ToString(CultureInfo.InvariantCulture))
                .Append(", source_trust ").Append(SourceTrustName(input.SourceTrust)).Append('\n');

        return new DecisionFile(record.DecisionId, Slug(title), builder.ToString());
    }

    /// <summary>
    /// Lowercase ASCII name part for the file: letters and digits, runs of anything else become one hyphen, at most 60
    /// characters, never empty (falls back to <c>decision</c>).
    /// </summary>
    public static string Slug(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var slug = NonSlugRun().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > _maxSlugLength)
            slug = slug[.._maxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? _fallbackSlug : slug;
    }

    /// <summary>
    /// A YAML double-quoted scalar. JSON string syntax is a valid YAML double-quoted scalar; the relaxed encoder keeps quotes
    /// readable as <c>\"</c> instead of <c>"</c>.
    /// </summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value, _quoteOptions);

    private static readonly JsonSerializerOptions _quoteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string KindName(JudgmentKind kind) => kind switch
    {
        JudgmentKind.Promotion => "promotion",
        JudgmentKind.Hiring => "hiring",
        JudgmentKind.Retirement => "retirement",
        JudgmentKind.Charter => "charter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown judgment kind."),
    };

    private static string ConfidenceName(JudgmentConfidence confidence) => confidence switch
    {
        JudgmentConfidence.Low => "low",
        JudgmentConfidence.Medium => "medium",
        JudgmentConfidence.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Unknown confidence."),
    };

    private static string SourceTrustName(SourceTrust trust) => trust switch
    {
        SourceTrust.Owner => "owner",
        SourceTrust.System => "system",
        SourceTrust.MemberHuman => "member-human",
        SourceTrust.Agent => "agent",
        SourceTrust.External => "external",
        SourceTrust.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown source trust."),
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugRun();
}
