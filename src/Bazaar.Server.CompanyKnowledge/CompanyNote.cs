using System.Globalization;
using System.Text;
using Bazaar.Server.CompanyKnowledge.Abstractions;
using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>Server-derived evidence counts (doc 08, 6.6). Written by the server only.</summary>
public sealed record CompanyCounts(int ProjectsConfirming, int ProjectsContradicting, int SourcesTombstoned);

/// <summary>Write-back counts from <c>follows_precedent</c> links (doc 08, 6.6). Written by the server only.</summary>
public sealed record CompanyCountsApplied(int Applied, int AppliedFailed);

/// <summary>Occurrence record of a failure note (doc 08, 6.6).</summary>
public sealed record CompanyOccurrences(int Count, string FirstSeen, string LastSeen);

/// <summary>The closed relation set of a company note (doc 08, 6.2 rule 2; doc 64, 5.3).</summary>
public sealed record CompanyRelations(IReadOnlyList<string> ConditionedOn, IReadOnlyList<string> Affects, IReadOnlyList<string> ResolvedBy)
{
    /// <summary>No relations.</summary>
    public static CompanyRelations Empty { get; } = new([], [], []);
}

/// <summary>How a note was admitted (doc 08, 6.2). <c>By</c> is <c>review-window</c>, <c>council:&lt;motion&gt;</c> or <c>owner</c>.</summary>
public sealed record CompanyAdmitted(string By, string At, string Candidate);

/// <summary>Owner promotion to T3 (doc 08, 6.4).</summary>
public sealed record CompanyPromoted(string Tier, string By, string At, string? Commit);

/// <summary>Distillation provenance; absent for a direct proposal.</summary>
public sealed record CompanyDistilledBy(string Run, string? ReaderSession);

/// <summary>
/// The <c>company</c> front matter block of doc 08, section 6.2. <see cref="ProposedBy"/> is not in the doc's example; it is where this
/// module records the proposer so admission can record it and the candidate access rule can find it (see the module notes in doc 40).
/// </summary>
public sealed record CompanyBlock(
    CompanyKind Kind,
    string Statement,
    IReadOnlyList<string> AppliesWhen,
    IReadOnlyList<string> NotWhen,
    IReadOnlyList<CompanyEvidence> Evidence,
    string? ProposedBy,
    CompanyCounts? Counts,
    CompanyCountsApplied? CountsApplied,
    CompanyOccurrences? Occurrences,
    string? Confidence,
    string? ReviewBy,
    string? ValidTo,
    CompanyRelations Relations,
    CompanyAdmitted? Admitted,
    CompanyPromoted? Promoted,
    CompanyDistilledBy? DistilledBy);

/// <summary>A parsed company note: the shared memory front matter, the company block and the body.</summary>
/// <param name="Memory">The memory front matter (doc 18 keys plus section 1 keys).</param>
/// <param name="Company">The company block.</param>
/// <param name="Body">The Markdown body, byte for byte.</param>
public sealed record CompanyNote(MemoryNote Memory, CompanyBlock Company, string Body)
{
    /// <summary>Parses note text. Returns the note, or finding codes (never note text) when it is not a well-formed company note.</summary>
    public static CompanyParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parsed = NoteDocument.Parse(text);
        if (parsed.Document is not { } doc)
        {
            return CompanyParseResult.Fail(CompanyFinding.NotParseable);
        }

        var extras = doc.ExtraFields;
        if (extras.Any(e => e.Key != "company"))
        {
            return CompanyParseResult.Fail(CompanyFinding.UnknownTopLevelField);
        }

        if (extras.FirstOrDefault(e => e.Key == "company").Value is not FrontMapping mapping)
        {
            return CompanyParseResult.Fail(CompanyFinding.MissingCompanyBlock);
        }

        var findings = new List<CompanyFinding>();
        var block = CompanyBlockReader.Read(mapping, findings);
        return block is null || findings.Count > 0
            ? CompanyParseResult.Fail([.. findings])
            : CompanyParseResult.Success(new CompanyNote(doc.Note, block, doc.Body));
    }

    /// <summary>The note text: canonical memory front matter, the company block as a one-line flow mapping, then the body.</summary>
    public string Serialize()
    {
        var head = NoteDocument.Create(Memory, string.Empty).Serialize();
        var close = head.LastIndexOf("---\n", StringComparison.Ordinal);
        return head[..close] + "company: " + CompanyBlockWriter.Write(Company) + "\n---\n" + Body;
    }

    /// <summary>
    /// The read-time view for a caller who cannot read the evidence sources (doc 08, 6.5): each entry keeps role, status and
    /// independence and loses id, project, author, time and origin trust. The note's own counts stay.
    /// </summary>
    public CompanyNote RedactEvidence() => this with
    {
        Company = Company with
        {
            Evidence = [.. Company.Evidence.Select(e => new CompanyEvidence(null, null, e.Role, null, null, null, e.Independent, e.Status))],
        },
    };
}

/// <summary>Result of <see cref="CompanyNote.Parse"/>.</summary>
public sealed record CompanyParseResult
{
    private CompanyParseResult(CompanyNote? note, IReadOnlyList<CompanyFinding> findings)
    {
        Note = note;
        Findings = findings;
    }

    /// <summary>The note, or null on failure.</summary>
    public CompanyNote? Note { get; }

    /// <summary>Finding codes; empty on success.</summary>
    public IReadOnlyList<CompanyFinding> Findings { get; }

    internal static CompanyParseResult Success(CompanyNote note) => new(note, []);

    internal static CompanyParseResult Fail(params CompanyFinding[] findings) => new(null, findings);
}

internal static class CompanyBlockReader
{
    private static readonly string[] _keys =
    [
        "kind", "statement", "applies_when", "not_when", "evidence", "proposed_by", "counts", "counts_applied", "occurrences",
        "confidence", "review_by", "valid_to", "relations", "admitted", "promoted", "distilled_by",
    ];

    public static CompanyBlock? Read(FrontMapping map, List<CompanyFinding> findings)
    {
        var d = map.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        if (d.Keys.Any(k => !_keys.Contains(k, StringComparer.Ordinal)))
        {
            findings.Add(CompanyFinding.UnknownCompanyField);
        }

        if (!CompanyKinds.TryParse(Scalar(d, "kind"), out var kind))
        {
            findings.Add(CompanyFinding.KindInvalid);
            return null;
        }

        var evidence = new List<CompanyEvidence>();
        if (d.TryGetValue("evidence", out var ev))
        {
            if (ev is not FrontSequence seq)
            {
                findings.Add(CompanyFinding.EvidenceMalformed);
            }
            else
            {
                foreach (var item in seq.Items)
                {
                    if (item is FrontMapping m && ReadEvidence(m) is { } entry)
                    {
                        evidence.Add(entry);
                    }
                    else
                    {
                        findings.Add(CompanyFinding.EvidenceMalformed);
                    }
                }
            }
        }

        return new CompanyBlock(
            kind,
            Scalar(d, "statement") ?? string.Empty,
            Strings(d, "applies_when", findings),
            Strings(d, "not_when", findings),
            evidence,
            Scalar(d, "proposed_by"),
            Counts(d, findings),
            Applied(d, findings),
            Occurrences(d, findings),
            Scalar(d, "confidence"),
            Scalar(d, "review_by"),
            Scalar(d, "valid_to"),
            Relations(d, findings),
            Map(d, "admitted") is { } a ? new CompanyAdmitted(Scalar(a, "by") ?? string.Empty, Scalar(a, "at") ?? string.Empty, Scalar(a, "candidate") ?? string.Empty) : null,
            Map(d, "promoted") is { } p ? new CompanyPromoted(Scalar(p, "tier") ?? string.Empty, Scalar(p, "by") ?? string.Empty, Scalar(p, "at") ?? string.Empty, Scalar(p, "commit")) : null,
            Map(d, "distilled_by") is { } db ? new CompanyDistilledBy(Scalar(db, "run") ?? string.Empty, Scalar(db, "reader_session")) : null);
    }

    private static CompanyEvidence? ReadEvidence(FrontMapping m)
    {
        var d = m.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var role = Scalar(d, "role") switch
        {
            "confirms" => (CompanyEvidenceRole?)CompanyEvidenceRole.Confirms,
            "contradicts" => CompanyEvidenceRole.Contradicts,
            _ => null,
        };
        var status = (Scalar(d, "status") ?? "live") switch
        {
            "live" => (CompanyEvidenceStatus?)CompanyEvidenceStatus.Live,
            "deprecated-source" => CompanyEvidenceStatus.DeprecatedSource,
            "tombstone" => CompanyEvidenceStatus.Tombstone,
            _ => null,
        };
        var independent = (Scalar(d, "independent") ?? "true") switch
        {
            "true" => (bool?)true,
            "false" => false,
            _ => null,
        };
        if (role is null || status is null || independent is null)
        {
            return null;
        }

        return new CompanyEvidence(Scalar(d, "doc_id"), Scalar(d, "project"), role.Value, Scalar(d, "author"), Scalar(d, "at"), Scalar(d, "origin_trust"), independent.Value, status.Value);
    }

    private static CompanyCounts? Counts(Dictionary<string, FrontValue> d, List<CompanyFinding> findings)
    {
        if (Map(d, "counts") is not { } m)
        {
            return null;
        }

        return Int(m, "projects_confirming", findings) is { } a && Int(m, "projects_contradicting", findings) is { } b && Int(m, "sources_tombstoned", findings) is { } c
            ? new CompanyCounts(a, b, c)
            : null;
    }

    private static CompanyCountsApplied? Applied(Dictionary<string, FrontValue> d, List<CompanyFinding> findings)
    {
        if (Map(d, "counts_applied") is not { } m)
        {
            return null;
        }

        return Int(m, "applied", findings) is { } a && Int(m, "applied_failed", findings) is { } b ? new CompanyCountsApplied(a, b) : null;
    }

    private static CompanyOccurrences? Occurrences(Dictionary<string, FrontValue> d, List<CompanyFinding> findings)
    {
        if (Map(d, "occurrences") is not { } m)
        {
            return null;
        }

        return Int(m, "count", findings) is { } n ? new CompanyOccurrences(n, Scalar(m, "first_seen") ?? string.Empty, Scalar(m, "last_seen") ?? string.Empty) : null;
    }

    private static CompanyRelations Relations(Dictionary<string, FrontValue> d, List<CompanyFinding> findings)
    {
        if (!d.TryGetValue("relations", out var value))
        {
            return CompanyRelations.Empty;
        }

        if (value is not FrontMapping m)
        {
            findings.Add(CompanyFinding.RelationsMalformed);
            return CompanyRelations.Empty;
        }

        var rel = m.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        if (rel.Keys.Any(k => k is not ("conditioned_on" or "affects" or "resolved_by")))
        {
            findings.Add(CompanyFinding.RelationKindUnknown);
        }

        return new CompanyRelations(Strings(rel, "conditioned_on", findings), Strings(rel, "affects", findings), Strings(rel, "resolved_by", findings));
    }

    private static Dictionary<string, FrontValue>? Map(Dictionary<string, FrontValue> d, string key) =>
        d.TryGetValue(key, out var v) && v is FrontMapping m ? m.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) : null;

    private static string? Scalar(Dictionary<string, FrontValue> d, string key) =>
        d.TryGetValue(key, out var v) && v is FrontScalar s ? s.Text : null;

    private static int? Int(Dictionary<string, FrontValue> d, string key, List<CompanyFinding> findings)
    {
        if (Scalar(d, key) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return n;
        }

        findings.Add(CompanyFinding.CountMalformed);
        return null;
    }

    private static List<string> Strings(Dictionary<string, FrontValue> d, string key, List<CompanyFinding> findings)
    {
        var list = new List<string>();
        if (!d.TryGetValue(key, out var v))
        {
            return list;
        }

        if (v is not FrontSequence seq || seq.Items.Any(i => i is not FrontScalar))
        {
            findings.Add(CompanyFinding.ListMalformed);
            return list;
        }

        list.AddRange(seq.Items.Cast<FrontScalar>().Select(s => s.Text));
        return list;
    }
}

internal static class CompanyBlockWriter
{
    public static string Write(CompanyBlock b)
    {
        var parts = new List<string>
        {
            "kind: " + Q(CompanyKinds.Name(b.Kind)),
            "statement: " + Q(b.Statement),
            "applies_when: " + List(b.AppliesWhen),
        };
        if (b.NotWhen.Count > 0)
        {
            parts.Add("not_when: " + List(b.NotWhen));
        }

        parts.Add("evidence: [" + string.Join(", ", b.Evidence.Select(Evidence)) + "]");
        Add(parts, "proposed_by", b.ProposedBy);
        if (b.Counts is { } c)
        {
            parts.Add($"counts: {{projects_confirming: {N(c.ProjectsConfirming)}, projects_contradicting: {N(c.ProjectsContradicting)}, sources_tombstoned: {N(c.SourcesTombstoned)}}}");
        }

        if (b.CountsApplied is { } ca)
        {
            parts.Add($"counts_applied: {{applied: {N(ca.Applied)}, applied_failed: {N(ca.AppliedFailed)}}}");
        }

        if (b.Occurrences is { } o)
        {
            parts.Add($"occurrences: {{count: {N(o.Count)}, first_seen: {Q(o.FirstSeen)}, last_seen: {Q(o.LastSeen)}}}");
        }

        Add(parts, "confidence", b.Confidence);
        Add(parts, "review_by", b.ReviewBy);
        Add(parts, "valid_to", b.ValidTo);
        var r = b.Relations;
        if (r.ConditionedOn.Count + r.Affects.Count + r.ResolvedBy.Count > 0)
        {
            var rel = new List<string>();
            if (r.ConditionedOn.Count > 0)
            {
                rel.Add("conditioned_on: " + List(r.ConditionedOn));
            }

            if (r.Affects.Count > 0)
            {
                rel.Add("affects: " + List(r.Affects));
            }

            if (r.ResolvedBy.Count > 0)
            {
                rel.Add("resolved_by: " + List(r.ResolvedBy));
            }

            parts.Add("relations: {" + string.Join(", ", rel) + "}");
        }

        if (b.Admitted is { } a)
        {
            parts.Add($"admitted: {{by: {Q(a.By)}, at: {Q(a.At)}, candidate: {Q(a.Candidate)}}}");
        }

        if (b.Promoted is { } p)
        {
            parts.Add($"promoted: {{tier: {Q(p.Tier)}, by: {Q(p.By)}, at: {Q(p.At)}{(p.Commit is null ? string.Empty : ", commit: " + Q(p.Commit))}}}");
        }

        if (b.DistilledBy is { } d)
        {
            parts.Add($"distilled_by: {{run: {Q(d.Run)}{(d.ReaderSession is null ? string.Empty : ", reader_session: " + Q(d.ReaderSession))}}}");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    private static string Evidence(CompanyEvidence e)
    {
        var parts = new List<string>();
        Add(parts, "doc_id", e.DocId);
        Add(parts, "project", e.Project);
        parts.Add("role: " + (e.Role == CompanyEvidenceRole.Confirms ? "confirms" : "contradicts"));
        Add(parts, "author", e.Author);
        Add(parts, "at", e.At);
        Add(parts, "origin_trust", e.OriginTrust);
        parts.Add("independent: " + (e.Independent ? "true" : "false"));
        parts.Add("status: " + e.Status switch
        {
            CompanyEvidenceStatus.Live => "live",
            CompanyEvidenceStatus.DeprecatedSource => "deprecated-source",
            _ => "tombstone",
        });
        return "{" + string.Join(", ", parts) + "}";
    }

    private static void Add(List<string> parts, string key, string? value)
    {
        if (value is not null)
        {
            parts.Add(key + ": " + Q(value));
        }
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string List(IReadOnlyList<string> items) => "[" + string.Join(", ", items.Select(Q)) + "]";

    private static string Q(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
