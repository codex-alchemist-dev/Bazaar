namespace Bazaar.Server.Subagents;

/// <summary>What the spawner knows about the parent: the limits a child must stay inside.</summary>
/// <param name="ParentId">The parent session id.</param>
/// <param name="ChainId">The root of the parent chain; the concurrency cap is counted per chain.</param>
/// <param name="ToolAllowlist">The parent's own tool allowlist.</param>
/// <param name="ModelWhitelist">The parent's model whitelist (doc 05 deny-by-default).</param>
/// <param name="Depth">The parent's depth: 0 for a top-level session, 1 for its child, and so on.</param>
public sealed record ParentContext(
    string ParentId,
    string ChainId,
    IReadOnlySet<string> ToolAllowlist,
    IReadOnlySet<string> ModelWhitelist,
    int Depth);
