namespace Bazaar.Server.Memory.Write;

/// <summary>Builds the commit message for a memory batch. Output depends only on the batch, so it is deterministic.</summary>
public static class MemoryCommitMessage
{
    /// <summary>
    /// Returns the subject line, a blank line, then the trailers in fixed order:
    /// <c>Bazaar-Agent</c>, <c>Bazaar-GC</c>, <c>Bazaar-Task</c>, <c>Bazaar-Memory-Ops</c>,
    /// <c>Bazaar-Memory-Validation</c>. Lines are separated by a line feed with no trailing newline.
    /// </summary>
    public static string Build(MemoryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateSingleLine(batch.AgentId, nameof(batch.AgentId));
        ValidateSingleLine(batch.GcId, nameof(batch.GcId));
        ValidateSingleLine(batch.TaskId, nameof(batch.TaskId));
        ValidateSingleLine(batch.ValidationSummary, nameof(batch.ValidationSummary));

        var count = batch.Items.Count;
        return string.Join('\n',
            $"Memory: {count} operation(s) for {batch.AgentId}",
            string.Empty,
            $"Bazaar-Agent: {batch.AgentId}",
            $"Bazaar-GC: {batch.GcId}",
            $"Bazaar-Task: {batch.TaskId}",
            $"Bazaar-Memory-Ops: {count}",
            $"Bazaar-Memory-Validation: {batch.ValidationSummary}");
    }

    private static void ValidateSingleLine(string value, string name)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Trailer values must be single-line.", name);
        }
    }
}
