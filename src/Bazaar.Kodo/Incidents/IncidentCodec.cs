using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Incidents;

/// <summary>
/// Source-generated JSON for incident state, evidence manifests and summaries. camelCase, compact, enums as words, and unknown members
/// are rejected so a damaged or foreign file is not half-read.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IncidentRecord))]
[JsonSerializable(typeof(EvidenceManifest))]
[JsonSerializable(typeof(IncidentSummary))]
internal sealed partial class IncidentJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and writes incident records, manifests and summaries as UTF-8 JSON. Parsing never throws on bad input.</summary>
public static class IncidentCodec
{
    /// <summary>Input larger than this is refused before parsing. PROPOSED.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Encodes a record.</summary>
    public static byte[] ToUtf8Bytes(IncidentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.SerializeToUtf8Bytes(record, IncidentJsonContext.Default.IncidentRecord);
    }

    /// <summary>Encodes a manifest.</summary>
    public static byte[] ToUtf8Bytes(EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, IncidentJsonContext.Default.EvidenceManifest);
    }

    /// <summary>Encodes a summary.</summary>
    public static byte[] ToUtf8Bytes(IncidentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return JsonSerializer.SerializeToUtf8Bytes(summary, IncidentJsonContext.Default.IncidentSummary);
    }

    /// <summary>Parses a saved record. False for oversized, malformed, wrong-version, unknown-member or inconsistent input.</summary>
    public static bool TryParseRecord(ReadOnlySpan<byte> utf8, out IncidentRecord? record)
    {
        record = null;
        if (utf8.Length == 0 || utf8.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            IncidentRecord? parsed = JsonSerializer.Deserialize(utf8, IncidentJsonContext.Default.IncidentRecord);
            if (parsed is null || !IsSane(parsed))
            {
                return false;
            }

            record = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses a manifest and checks its chain. False when malformed or when any hash does not match.</summary>
    public static bool TryParseManifest(ReadOnlySpan<byte> utf8, out EvidenceManifest? manifest)
    {
        manifest = null;
        if (utf8.Length == 0 || utf8.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            EvidenceManifest? parsed = JsonSerializer.Deserialize(utf8, IncidentJsonContext.Default.EvidenceManifest);
            if (parsed is null || parsed.Version != EvidenceManifest.CurrentVersion || parsed.Items is null
                || parsed.Items.Count > 256 || string.IsNullOrEmpty(parsed.IncidentId) || EvidenceManifest.FirstBroken(parsed) is not null)
            {
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSane(IncidentRecord r)
    {
        if (r.Version != IncidentRecord.CurrentVersion || r.Number < 1 || r.Sequence < 0
            || !string.Equals(r.Id, IncidentRecord.IdFor(r.Number), StringComparison.Ordinal)
            || !Enum.IsDefined(r.Severity) || !Enum.IsDefined(r.Trigger) || !Enum.IsDefined(r.Phase)
            || !IncidentText.IsIdentifier(r.Cause) || !IncidentText.IsTitle(r.Title)
            || !IncidentText.IsIdentifier(r.Opener) || !IncidentText.IsIdentifier(r.OpenReason)
            || r.Categories is null || r.Categories.Count > IncidentRecord.MaxCategories || !r.Categories.All(IncidentText.IsComponent)
            || r.LinkedAlerts is null || r.LinkedAlerts.Count > IncidentRecord.MaxLinkedAlerts || !r.LinkedAlerts.All(IncidentText.IsIdentifier)
            || r.Plan is null || r.Plan.Count > IncidentRecord.MaxPlan
            || r.Runs is null || r.Runs.Count > IncidentRecord.MaxRuns
            || r.Transitions is null || r.Transitions.Count > IncidentRecord.MaxTransitions
            || r.EvidenceItems < 0)
        {
            return false;
        }

        if (r.Plan.Any(p => p is null || !IncidentText.IsComponent(p.RunbookId) || p.Version < 1 || p.Title is null || p.Title.Length > 120)
            || r.Runs.Any(run => run is null || !IncidentText.IsIdentifier(run.RunId) || !IncidentText.IsComponent(run.RunbookId) || !Enum.IsDefined(run.Phase)))
        {
            return false;
        }

        IncidentPhase expected = IncidentPhase.Opened;
        for (int i = 0; i < r.Transitions.Count; i++)
        {
            IncidentTransition t = r.Transitions[i];
            if (t is null || t.Index != i || t.From != expected || !IncidentStateMachine.IsValid(t.From, t.To)
                || !IncidentText.IsIdentifier(t.Actor) || !IncidentText.IsIdentifier(t.Reason)
                || (t.ApprovalId is not null && !IncidentText.IsIdentifier(t.ApprovalId)))
            {
                return false;
            }

            expected = t.To;
        }

        return r.Phase == expected;
    }
}
