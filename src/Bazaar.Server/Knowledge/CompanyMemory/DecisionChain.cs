namespace Bazaar.Server.Knowledge.CompanyMemory;

/// <summary>One decision record as the chain needs it: its Vault path and the front matter fields that link decisions.</summary>
/// <param name="Path">The Vault path, such as <c>decisions/0002-use-notify.md</c>.</param>
/// <param name="Status">The record's status: <c>proposed</c>, <c>accepted</c> or <c>superseded</c>.</param>
/// <param name="Supersedes">Decisions this record replaces, as written in front matter (<c>0001-slug</c> or a path).</param>
/// <param name="SupersededBy">The decision that replaces this one, as written in front matter, or null.</param>
/// <param name="DecidedIn">The GC message reference that decided the record, or null. Not used by the chain itself.</param>
public sealed record DecisionRecord(
    string Path,
    string Status,
    IReadOnlyList<string> Supersedes,
    string? SupersededBy,
    string? DecidedIn);

/// <summary>
/// One edge of the decision chain (doc 25 <c>decision_links</c>). <see cref="FromDoc"/> is the newer decision and
/// <see cref="ToDoc"/> the one it replaces, so every edge reads "from supersedes to".
/// </summary>
/// <param name="FromDoc">The Vault path of the superseding decision.</param>
/// <param name="ToDoc">The Vault path of the superseded decision. Set even when that record is missing.</param>
/// <param name="Relation">Always <see cref="DecisionChain.SupersedesRelation"/> for now.</param>
/// <param name="Status">The status of <see cref="FromDoc"/> when that record is in the input; null when it is missing.</param>
/// <param name="FromMissing">True when <see cref="FromDoc"/> is not among the decision records (a "missing source").</param>
/// <param name="ToMissing">True when <see cref="ToDoc"/> is not among the decision records (an unresolved target).</param>
public sealed record DecisionLink(
    string FromDoc,
    string ToDoc,
    string Relation,
    string? Status,
    bool FromMissing,
    bool ToMissing);

/// <summary>
/// Derives the decision chain from decision records (doc 25 WP5). Pure: no SQL, no clock, no I/O. The Vault stays the
/// source; this is a rebuildable index over it.
/// </summary>
/// <remarks>
/// Both front matter directions become one edge shape. A record's <c>supersedes</c> entry gives an edge from the record to
/// the target; its <c>superseded_by</c> entry gives an edge from that target to the record. Both are stored as
/// "supersedes" edges, so a pair that declares the same fact from both sides yields one edge. Targets that are not in the
/// input are kept and flagged, so the view can show a missing source instead of losing the link.
/// </remarks>
public static class DecisionChain
{
    /// <summary>The only relation stored today.</summary>
    public const string SupersedesRelation = "supersedes";

    private const string _decisionsPrefix = "decisions/";
    private const string _markdownSuffix = ".md";
    private const char _separator = '\u001f';

    /// <summary>
    /// Returns the chain edges, deduplicated and sorted by from, to, then relation (ordinal). The result depends only on the
    /// set of records, not on their input order.
    /// </summary>
    /// <exception cref="ArgumentException">Two records share a path, or a reference is blank.</exception>
    public static IReadOnlyList<DecisionLink> Derive(IEnumerable<DecisionRecord> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var byPath = new Dictionary<string, DecisionRecord>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!byPath.TryAdd(decision.Path, decision))
            {
                throw new ArgumentException($"Two decision records share the path '{decision.Path}'.", nameof(decisions));
            }
        }

        var edges = new Dictionary<string, DecisionLink>(StringComparer.Ordinal);
        foreach (var decision in byPath.Values)
        {
            foreach (var target in decision.Supersedes)
            {
                AddEdge(edges, byPath, from: decision.Path, to: TargetPath(target));
            }

            if (decision.SupersededBy is not null)
            {
                AddEdge(edges, byPath, from: TargetPath(decision.SupersededBy), to: decision.Path);
            }
        }

        return edges.Values
            .OrderBy(link => link.FromDoc, StringComparer.Ordinal)
            .ThenBy(link => link.ToDoc, StringComparer.Ordinal)
            .ThenBy(link => link.Relation, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Adds one supersedes edge unless an edge with the same from, to and relation already exists.</summary>
    private static void AddEdge(
        Dictionary<string, DecisionLink> edges,
        Dictionary<string, DecisionRecord> byPath,
        string from,
        string to)
    {
        var key = string.Join(_separator, from, to, SupersedesRelation);
        if (edges.ContainsKey(key))
        {
            return;
        }

        var fromRecord = byPath.GetValueOrDefault(from);
        edges[key] = new DecisionLink(
            FromDoc: from,
            ToDoc: to,
            Relation: SupersedesRelation,
            Status: fromRecord?.Status,
            FromMissing: fromRecord is null,
            ToMissing: !byPath.ContainsKey(to));
    }

    /// <summary>
    /// Turns a front matter reference into a Vault path. <c>0001-slug</c>, <c>decisions/0001-slug</c> and
    /// <c>decisions/0001-slug.md</c> all name the same record.
    /// </summary>
    private static string TargetPath(string reference)
    {
        var trimmed = reference.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A decision reference must not be blank.", nameof(reference));
        }

        if (trimmed.StartsWith(_decisionsPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[_decisionsPrefix.Length..];
        }

        if (trimmed.EndsWith(_markdownSuffix, StringComparison.Ordinal))
        {
            trimmed = trimmed[..^_markdownSuffix.Length];
        }

        return _decisionsPrefix + trimmed + _markdownSuffix;
    }
}
