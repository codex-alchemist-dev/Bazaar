namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>
/// The sections of a turn's context, in assembly order (doc 06 "Context assembly"). The declaration order is the
/// order the model sees and the order the manifest lists.
/// </summary>
public enum ContextSection
{
    /// <summary>Role, Constitution excerpt, Soul personality and tool schemas. A fixed, cache-friendly prefix.</summary>
    System,

    /// <summary>Vault notes ranked by the memory index, with provenance ids.</summary>
    Memory,

    /// <summary>Current Kanban card, blockers and PR status.</summary>
    TaskState,

    /// <summary>Wiki-linked Vault documents for the task, capped.</summary>
    Vault,

    /// <summary>The trigger thread in full, and other GCs summarised. The trigger thread is never dropped.</summary>
    GcBackread,

    /// <summary>What woke the agent, verbatim. Not budgeted.</summary>
    Inbox,
}
