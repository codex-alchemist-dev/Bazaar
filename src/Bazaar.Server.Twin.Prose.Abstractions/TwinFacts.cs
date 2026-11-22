namespace Bazaar.Server.Twin.Prose;

/// <summary>Trust tier of a Twin fact (doc 08 section 4). Ordered, so the higher value is the more trusted.</summary>
public enum TwinTrust
{
    /// <summary>Never delivered to a reader session.</summary>
    Quarantined = 0,

    /// <summary>From external or tainted input. Passed to the reader only as quoted data.</summary>
    UntrustedDerived = 1,

    /// <summary>Clean agent or system write.</summary>
    AgentVerified = 2,

    /// <summary>Human-approved.</summary>
    OwnerPromoted = 3,
}

/// <summary>A pointer to one input of a fact. <see cref="Key"/> is the string a prose sentence cites.</summary>
public sealed record TwinSourceRef(string Kind, string Ref, string? Commit = null)
{
    /// <summary>The citation form, <c>kind:ref</c>. The commit is not part of it.</summary>
    public string Key => $"{Kind}:{Ref}";
}

/// <summary>One fact read for the prose step. The text fields can carry untrusted content, so they are only ever passed on as quoted data.</summary>
/// <param name="Id">Stable fact id. A sentence may cite it.</param>
/// <param name="OriginKind">Origin label from doc 15's provenance enum, as text (the repo-file mapping is open in doc 25).</param>
public sealed record TwinFactRecord(
    string Id,
    string ProjectId,
    string Kind,
    string Subject,
    string Value,
    TwinTrust Trust,
    string OriginKind,
    IReadOnlyList<TwinSourceRef> SourceRefs);
