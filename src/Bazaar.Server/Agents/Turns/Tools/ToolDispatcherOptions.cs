using System.Collections.Immutable;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Operator choices for the dispatcher (doc 06 settings). The defaults are the most protective: no tool is open to a
/// reader turn until an operator names it.
/// </summary>
public sealed record ToolDispatcherOptions
{
    /// <summary>The shipped default: every tool is refused in a reader turn.</summary>
    public static ToolDispatcherOptions Default { get; } = new();

    /// <summary>Tools a reader turn may call. Each must also be a read tool in the policy registry, or the policy refuses it.</summary>
    public IReadOnlySet<string> ReaderReadOnlyTools { get; init; } = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}
