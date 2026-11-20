namespace Bazaar.Server.Reputation;

/// <summary>Kinds of reputation evidence. Each kind maps to one weight in <see cref="WeightPolicy"/>.</summary>
public enum EvidenceKind
{
    OwnerAccept,
    OwnerReject,
    Revert,
    PostMergeDefect,
    CiFirstRun,
    PeerReview,
    Manual,
}
