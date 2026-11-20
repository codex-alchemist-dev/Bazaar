namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Where a ballot prompt runs. A Thinking GC never shows vote reasoning before the reveal (docs/design/10-council-and-voting.md,
/// Private storage and reveal; doc 06, C11).
/// </summary>
public enum PromptContext
{
    /// <summary>A working GC or the agent's normal work session.</summary>
    Working,

    /// <summary>A Thinking GC, nested under a working GC.</summary>
    Thinking,
}
