namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Trust tag the server assigns at ingestion (doc 15, provenance and taint table). The names mirror
/// <c>Bazaar.Contracts.Gc.SourceTrust</c>; a boundary mapping replaces this copy once the server
/// references Contracts.
/// </summary>
public enum ContentTrust
{
    /// <summary>Authenticated owner.</summary>
    Owner,

    /// <summary>Server-generated.</summary>
    System,

    /// <summary>Authenticated co-member who is not the owner.</summary>
    MemberHuman,

    /// <summary>Another agent's message or output, including subagent output.</summary>
    Agent,

    /// <summary>Email, strangers' issues and PRs, web, forums, package text, and their tool output.</summary>
    External,

    /// <summary>Flagged content. Never delivered to a session.</summary>
    Quarantined,
}
