namespace Bazaar.Server.Memory.Trust;

/// <summary>
/// Who is asking for a tier change. Only humans may promote (doc 08, section 5); votes and peer reviews
/// are recorded as actors so they can be refused explicitly.
/// </summary>
public enum MemoryActor
{
    /// <summary>The authenticated owner.</summary>
    Owner,

    /// <summary>An authenticated co-member who is not the owner.</summary>
    MemberHuman,

    /// <summary>An agent, including a request it makes for promotion.</summary>
    Agent,

    /// <summary>Server-generated work, such as consolidation.</summary>
    System,

    /// <summary>A council vote outcome (doc 10).</summary>
    CouncilVote,

    /// <summary>A peer review outcome (doc 11).</summary>
    PeerReview,
}
