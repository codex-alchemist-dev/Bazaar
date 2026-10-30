namespace Bazaar.Contracts.Graph;

/// <summary>
/// The node and edge kinds of doc 19. Kinds travel as strings so a later doc can add one with a change here; anything
/// outside these sets is rejected when a projection is built.
/// </summary>
public static class Known
{
    /// <summary>
    /// Node kinds: the Vault document types (doc 18 list), plus draft, pr, task, gc, screenshot, commit and branch.
    /// </summary>
    public static IReadOnlySet<string> NodeKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Vault document types (doc 18).
        "index", "plan", "deep-dive", "research", "constitution", "decision", "archive", "memory", "summary", "twin", "charter", "draft",
        // Other node kinds.
        "pr", "task", "gc", "screenshot", "commit", "branch",
    };

    /// <summary>Edge kinds of doc 19.</summary>
    public static IReadOnlySet<string> EdgeKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "links_to", "backlink", "decides", "implements", "mentions", "derived_from", "supersedes", "contradicts", "parent", "merges",
    };
}
