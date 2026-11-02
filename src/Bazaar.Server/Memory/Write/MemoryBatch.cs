namespace Bazaar.Server.Memory.Write;

/// <summary>
/// One agent's queued writes, flushed as a single commit. The trailer values are summaries
/// of the items: distinct GC and task ids, and the distinct validation results.
/// </summary>
/// <param name="AgentId">Agent whose private memory subtree receives the commit.</param>
/// <param name="Items">Writes in queue order.</param>
/// <param name="GcId">GC trailer value; <c>none</c> when no item names a GC.</param>
/// <param name="TaskId">Task trailer value; <c>none</c> when no item names a task.</param>
/// <param name="ValidationSummary">Validation trailer value; <c>none</c> when empty.</param>
public sealed record MemoryBatch(
    string AgentId,
    IReadOnlyList<MemoryWriteItem> Items,
    string GcId,
    string TaskId,
    string ValidationSummary)
{
    /// <summary>Builds a batch from items, summarising the trailer values deterministically.</summary>
    public static MemoryBatch Create(string agentId, IReadOnlyList<MemoryWriteItem> items)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(items);

        return new MemoryBatch(
            agentId,
            items,
            Summarise(items.Select(i => i.GcId), ", "),
            Summarise(items.Select(i => i.TaskId), ", "),
            Summarise(items.Select(i => i.ValidationSummary), "; "));
    }

    private static string Summarise(IEnumerable<string?> values, string separator)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return distinct.Count == 0 ? "none" : string.Join(separator, distinct);
    }
}
