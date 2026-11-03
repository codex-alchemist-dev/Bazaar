namespace Bazaar.Server.Gateway;

/// <summary>Delegated-route mapping for <c>gateway.delegated_route.mapping</c> (doc 05 "Settings").</summary>
public enum DelegatedRouteMapping
{
    OneRoutePerProfile,
    OneSharedRoute,
}

/// <summary>Claude lane source order for <c>gateway.claude.order</c> (doc 05 "Settings").</summary>
public enum ClaudeOrder
{
    NativeFirst,
    PooledFirst,
}

/// <summary>Gemini source order for <c>gateway.gemini.order</c> (doc 05 "Settings").</summary>
public enum GeminiOrder
{
    PaidFirst,
    FreeFirst,
}

/// <summary>Free-tier terms acknowledgement for <c>gateway.free_tier.ack_mode</c> (doc 05 "Settings"; needs counsel).</summary>
public enum FreeTierAckMode
{
    Required,
    Recorded,
}
