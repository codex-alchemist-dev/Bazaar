namespace Bazaar.Server.Fun.Ward;

/// <summary>What admitted an agent to the Psych Ward (doc 26, section 1). A classifier hit alone is not a trigger.</summary>
public enum WardTrigger
{
    /// <summary>Any loop-guard trip (doc 06, C34).</summary>
    LoopGuard,

    /// <summary>Three consecutive failed outcomes on one tag (doc 11).</summary>
    RepeatedFailure,

    /// <summary>A denied call or canary anomaly (doc 15).</summary>
    DeniedCallOrCanary,

    /// <summary>A token hard stop from the shared limits (doc 15).</summary>
    TokenHardStop,

    /// <summary>The agent asked for admission itself.</summary>
    SelfReferral,

    /// <summary>The owner referred the agent.</summary>
    OwnerReferral,
}
