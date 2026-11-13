namespace Bazaar.Kodo.Journal;

/// <summary>
/// One Kodo journal record (doc 46 section 6.5). Its hash covers the previous record's hash
/// and the canonical bytes of this record's fields (see <see cref="JournalCodec"/>).
/// </summary>
public sealed record JournalRecord
{
    /// <summary>Position in the chain, starting at 0.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the observation or state change was recorded. Always UTC after <see cref="JournalChain.Create"/>.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Record kind, for example a state change or an observation. 1 to 65535 UTF-8 bytes.</summary>
    public required string Kind { get; init; }

    /// <summary>Opaque payload bytes, covered by the hash.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Hash of the previous record, or 32 zero bytes for the first record.</summary>
    public required byte[] PreviousHash { get; init; }

    /// <summary>SHA-256 over <see cref="PreviousHash"/> followed by the canonical bytes of this record.</summary>
    public required byte[] Hash { get; init; }
}
