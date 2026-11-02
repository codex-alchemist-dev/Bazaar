using System.Collections.Immutable;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Readers;

/// <summary>Why a payload was refused. Stable codes for the audit log and for tests.</summary>
public enum HandoverRejectCode
{
    /// <summary>No schema with that id is registered.</summary>
    UnknownSchema,

    /// <summary>The payload, or a field value, has the wrong JSON type.</summary>
    WrongType,

    /// <summary>A required field is absent.</summary>
    MissingField,

    /// <summary>The payload carries a field the schema does not declare.</summary>
    UnknownField,

    /// <summary>The same field name appears twice.</summary>
    DuplicateField,

    /// <summary>The payload is larger than 64 KB of UTF-8 JSON.</summary>
    TooLarge,

    /// <summary>The payload nests containers deeper than 8 levels.</summary>
    TooDeep,

    /// <summary>A string is longer than its declared maximum.</summary>
    TooLong,

    /// <summary>An array has more items than its declared maximum.</summary>
    TooManyItems,

    /// <summary>An integer is outside its declared range, or is not a whole number.</summary>
    OutOfRange,

    /// <summary>A string is not one of the declared enum values.</summary>
    BadEnumValue,

    /// <summary>A URL field is not an absolute http or https URL.</summary>
    BadUrl,
}

/// <summary>
/// The outcome of validating one payload: accepted with only its declared, validated fields, or rejected with a reason.
/// </summary>
public abstract record HandoverResult
{
    /// <summary>The payload passed. Carries no unvalidated text.</summary>
    /// <param name="Handover">The validated hand-over.</param>
    public sealed record Accepted(AcceptedHandover Handover) : HandoverResult;

    /// <summary>The payload failed a rule. Nothing from it is delivered to an actor.</summary>
    /// <param name="Code">Stable reason code.</param>
    /// <param name="Detail">Human-readable detail for the audit log. Never contains the payload's text.</param>
    public sealed record Rejected(HandoverRejectCode Code, string Detail) : HandoverResult;
}

/// <summary>
/// A validated hand-over. Constructed only by <see cref="HandoverValidator"/>. It carries the agent trust tag, no
/// taint sources (doc 15), and lineage to the reader's source items once the store stamps it with
/// <see cref="WithLineage"/>.
/// </summary>
public sealed class AcceptedHandover
{
    private AcceptedHandover(string schemaId, ImmutableDictionary<string, HandoverValue> fields, ImmutableArray<Guid> derivedFrom)
    {
        SchemaId = schemaId;
        Fields = fields;
        DerivedFrom = derivedFrom;
    }

    /// <summary>The schema the payload was validated against.</summary>
    public string SchemaId { get; }

    /// <summary>Validated fields only. Unknown fields never reach this dictionary; the validator rejects them.</summary>
    public ImmutableDictionary<string, HandoverValue> Fields { get; }

    /// <summary>Trust tag. A validated hand-over is written as agent provenance (doc 15).</summary>
    public ContentTrust Trust { get; } = ContentTrust.Agent;

    /// <summary>Always empty: the validator does not taint the actor that receives the hand-over (doc 15).</summary>
    public ImmutableArray<TaintSource> TaintSources { get; } = ImmutableArray<TaintSource>.Empty;

    /// <summary>Lineage: the reader source items this hand-over was derived from. Empty until the store stamps it.</summary>
    public ImmutableArray<Guid> DerivedFrom { get; }

    /// <summary>Returns a copy that records the reader's source items as lineage.</summary>
    /// <exception cref="ArgumentException">No source items are given.</exception>
    public AcceptedHandover WithLineage(IEnumerable<Guid> sourceRefs)
    {
        ArgumentNullException.ThrowIfNull(sourceRefs);
        var refs = sourceRefs.ToImmutableArray();
        if (refs.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Lineage needs at least one source item.", nameof(sourceRefs));
        }

        return new AcceptedHandover(SchemaId, Fields, refs);
    }

    /// <summary>Creates an accepted hand-over. Internal, so only the validator in this assembly can produce one.</summary>
    internal static AcceptedHandover Create(string schemaId, ImmutableDictionary<string, HandoverValue> fields) =>
        new(schemaId, fields, ImmutableArray<Guid>.Empty);
}
