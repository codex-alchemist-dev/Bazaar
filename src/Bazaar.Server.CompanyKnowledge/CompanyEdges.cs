using Bazaar.Server.CompanyKnowledge.Abstractions;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>The typed edges a company note contributes to the graph (docs/design/08-memory-system.md, 6.2 and 6.8).</summary>
public enum CompanyEdgeKind
{
    /// <summary><c>memory.derived_from</c>: company targets only.</summary>
    DerivedFrom,

    /// <summary><c>memory.supersedes</c>: company targets only.</summary>
    Supersedes,

    /// <summary><c>company.relations.conditioned_on</c>.</summary>
    ConditionedOn,

    /// <summary><c>company.relations.affects</c>.</summary>
    Affects,

    /// <summary><c>company.relations.resolved_by</c>: a fix, decision or charter, never a person.</summary>
    ResolvedBy,

    /// <summary><c>company.evidence</c>: provenance only. Weight 0, not in the CSR, never expands, carries no PageRank mass.</summary>
    Evidence,
}

/// <summary>One edge from a company note.</summary>
/// <param name="Kind">The edge kind.</param>
/// <param name="Target">A company note id or <c>company/</c> path; for <see cref="CompanyEdgeKind.Evidence"/> the source <c>doc_id</c>.</param>
/// <param name="Expands">False for evidence edges: they are provenance and never followed in retrieval.</param>
public sealed record CompanyEdge(CompanyEdgeKind Kind, string Target, bool Expands);

/// <summary>Extracts the typed edges of a company note. Evidence entries without a source id (tombstones) yield no edge.</summary>
public static class CompanyEdges
{
    /// <summary>The edges of <paramref name="note"/>, in a stable order.</summary>
    public static IReadOnlyList<CompanyEdge> From(CompanyNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var edges = new List<CompanyEdge>();
        edges.AddRange(note.Memory.DerivedFrom.Select(t => new CompanyEdge(CompanyEdgeKind.DerivedFrom, t, true)));
        edges.AddRange(note.Memory.Supersedes.Select(t => new CompanyEdge(CompanyEdgeKind.Supersedes, t, true)));
        edges.AddRange(note.Company.Relations.ConditionedOn.Select(t => new CompanyEdge(CompanyEdgeKind.ConditionedOn, t, true)));
        edges.AddRange(note.Company.Relations.Affects.Select(t => new CompanyEdge(CompanyEdgeKind.Affects, t, true)));
        edges.AddRange(note.Company.Relations.ResolvedBy.Select(t => new CompanyEdge(CompanyEdgeKind.ResolvedBy, t, true)));
        edges.AddRange(note.Company.Evidence.Where(e => e.DocId is not null).Select(e => new CompanyEdge(CompanyEdgeKind.Evidence, e.DocId!, false)));
        return edges;
    }
}
