namespace Bazaar.Tabula;

/// <summary>
/// One entry in a named hash chain. Actors are canonical principal text (<c>kind:id</c>); this library does not parse them.
/// <see cref="OnBehalfOf"/> is set when one principal acted for another (doc 85 R-02). The hash covers every field here
/// except <see cref="Hash"/> itself.
/// </summary>
public sealed record ChainEntry(
    ChainId Chain,
    long Seq,
    DateTimeOffset Ts,
    string Actor,
    string? OnBehalfOf,
    string Kind,
    string Payload,
    byte[] PrevHash,
    byte[] Hash);

/// <summary>Why a chain failed verification.</summary>
public enum ChainFailure
{
    SequenceBroken,
    PrevHashMismatch,
    HashMismatch,
    HeadMismatch,
    WrongChain,
}

/// <summary>Result of verifying one chain. <see cref="FailedIndex"/> is the offending position in the input list, or -1 when valid.</summary>
public sealed record ChainVerification(ChainId Chain, bool IsValid, int Checked, int FailedIndex, long FailedSeq, ChainFailure? Failure)
{
    public static ChainVerification Ok(ChainId chain, int checkedCount) => new(chain, true, checkedCount, -1, 0, null);

    public static ChainVerification Fail(ChainId chain, int index, long seq, ChainFailure failure) =>
        new(chain, false, index, index, seq, failure);
}
