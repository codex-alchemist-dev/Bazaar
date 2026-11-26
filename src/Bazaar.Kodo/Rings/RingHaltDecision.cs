using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Rings;

/// <summary>
/// The record of one automatic halt (doc 46 section 16.6), shaped for the journal and the audit log. It holds identifiers and numbers
/// only: the release id, the rule ids, the module and probe ids, counts and rates. Anything that does not match the identifier shapes of
/// section 15.3 is replaced or dropped and counted in <see cref="RedactedCount"/>, so a halt can always be written and never leaks text.
/// The decision has no field for a rollback: a halt stops rollout and nothing else.
/// </summary>
/// <param name="DecisionId">A stable id derived from the digest, so the same decision always has the same id.</param>
/// <param name="ReleaseId">The halted release, or <c>redacted</c>.</param>
/// <param name="Channel">The channel.</param>
/// <param name="Ring">The ring that halted.</param>
/// <param name="DecidedAt">When it was decided.</param>
/// <param name="Reasons">The tripped rules.</param>
/// <param name="Modules">The critical modules with new contract failures, at most 8.</param>
/// <param name="Probes">The critical probes that were red, at most 8.</param>
/// <param name="Devices">Devices in the cohort.</param>
/// <param name="Sessions">Sessions in the cohort.</param>
/// <param name="RedactedCount">How many identifiers were replaced or dropped.</param>
/// <param name="Digest">The lowercase hex SHA-256 of the canonical content. A resume approval binds to it.</param>
public sealed record RingHaltDecision(
    string DecisionId,
    string ReleaseId,
    ReleaseChannel Channel,
    ReleaseRing Ring,
    DateTimeOffset DecidedAt,
    IReadOnlyList<HaltReason> Reasons,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Probes,
    int Devices,
    long Sessions,
    int RedactedCount,
    string Digest)
{
    /// <summary>What a halt does. Always this: rollout stops, installed releases are not touched.</summary>
    public const string Effect = "rollout_stopped";

    /// <summary>The placeholder for a release id that is not identifier-shaped.</summary>
    public const string RedactedValue = "redacted";

    /// <summary>The most module ids and probe ids a decision carries each.</summary>
    public const int MaxIdentifiers = 8;

    /// <summary>Builds the decision for a halt verdict. Throws when the evaluation is not a halt.</summary>
    public static RingHaltDecision Create(RingEvaluation evaluation, CohortSignals cohort, ReleaseChannel channel, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(cohort);
        if (evaluation.Verdict != RingVerdict.Halt)
        {
            throw new ArgumentException("A decision is only made for a halt.", nameof(evaluation));
        }

        int redacted = 0;
        string releaseId = KodoExportPatterns.IdentifierValue().IsMatch(cohort.ReleaseId) ? cohort.ReleaseId : RedactedValue;
        if (releaseId == RedactedValue && cohort.ReleaseId != RedactedValue)
        {
            redacted++;
        }

        List<string> modules = Clean(evaluation.NewCriticalModules, ref redacted);
        List<string> probes = Clean(cohort.RedCriticalProbes, ref redacted);
        List<HaltReason> reasons = evaluation.Reasons.Select(Round).ToList();

        RingHaltDecision draft = new(string.Empty, releaseId, channel, cohort.Ring, decidedAt, reasons, modules, probes,
            cohort.Devices, cohort.Sessions, redacted, string.Empty);
        string digest = Convert.ToHexStringLower(SHA256.HashData(draft.CanonicalBytes()));
        return draft with { DecisionId = "rh-" + digest[..24], Digest = digest };
    }

    /// <summary>
    /// The canonical UTF-8 bytes the digest covers: fixed key order, no insignificant whitespace, no id and no digest. Times are UTC
    /// seconds, so the same decision always hashes the same.
    /// </summary>
    public byte[] CanonicalBytes()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartObject();
            w.WriteString("releaseId", ReleaseId);
            w.WriteString("channel", Channel.ToString());
            w.WriteString("ring", Ring.ToString());
            w.WriteNumber("decidedAtUnix", DecidedAt.ToUnixTimeSeconds());
            w.WriteStartArray("reasons");
            foreach (HaltReason reason in Reasons)
            {
                w.WriteStartObject();
                w.WriteString("rule", reason.RuleId);
                w.WriteNumber("observed", reason.Observed);
                w.WriteNumber("threshold", reason.Threshold);
                w.WriteNumber("events", reason.Events);
                w.WriteNumber("denominator", reason.Denominator);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteStrings(w, "modules", Modules);
            WriteStrings(w, "probes", Probes);
            w.WriteNumber("devices", Devices);
            w.WriteNumber("sessions", Sessions);
            w.WriteNumber("redacted", RedactedCount);
            w.WriteString("effect", Effect);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Flat identifier fields for a journal or audit entry and for the webhook identifier shape (section 15.3): names match
    /// <c>[a-z][a-z0-9_]*</c> and values match <c>[A-Za-z0-9_.:-]{1,128}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToIdentifiers()
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["decision_id"] = DecisionId,
            ["release_id"] = ReleaseId,
            ["channel"] = Channel.ToString().ToLowerInvariant(),
            ["ring"] = Ring.ToString().ToLowerInvariant(),
            ["digest"] = Digest,
            ["effect"] = Effect,
            ["devices"] = Devices.ToString(CultureInfo.InvariantCulture),
            ["sessions"] = Sessions.ToString(CultureInfo.InvariantCulture),
            ["redacted"] = RedactedCount.ToString(CultureInfo.InvariantCulture),
        };
        AddIndexed(fields, "rule", Reasons.Select(reason => reason.RuleId));
        AddIndexed(fields, "module", Modules);
        AddIndexed(fields, "probe", Probes);
        return fields;
    }

    private static void AddIndexed(Dictionary<string, string> fields, string prefix, IEnumerable<string> values)
    {
        int index = 0;
        foreach (string value in values)
        {
            fields[$"{prefix}_{index++}"] = value;
        }
    }

    private static List<string> Clean(IReadOnlyList<string> ids, ref int redacted)
    {
        List<string> kept = [];
        foreach (string id in ids)
        {
            bool shaped = id is not null && id.Length <= 64 && KodoExportPatterns.ComponentId().IsMatch(id)
                          && !KodoExportPatterns.InstanceIdLike().IsMatch(id);
            if (!shaped || kept.Count >= MaxIdentifiers)
            {
                redacted++;
            }
            else if (!kept.Contains(id!, StringComparer.Ordinal))
            {
                kept.Add(id!);
            }
        }

        kept.Sort(StringComparer.Ordinal);
        return kept;
    }

    private static HaltReason Round(HaltReason reason) =>
        reason with { Observed = Math.Round(reason.Observed, 4), Threshold = Math.Round(reason.Threshold, 4) };

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (string value in values)
        {
            w.WriteStringValue(value);
        }

        w.WriteEndArray();
    }
}
