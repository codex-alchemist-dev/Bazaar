using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingStore;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.Host.Coding;

/// <summary>Binds the permission gate's <see cref="IToolCallTrace"/> port to the durable, append-only <c>tool_calls</c> table (doc 17 WP2).</summary>
public sealed class ToolCallLogTrace(IToolCallLog log) : IToolCallTrace
{
    /// <inheritdoc />
    public ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken) => log.AppendAsync(record, cancellationToken);
}

/// <summary>
/// Binds the subagent spawner's <see cref="ISubagentTrace"/> port to <c>tool_calls</c>. A spawn and a finish are each one row
/// under the parent session (tools <c>subagent.spawn</c> and <c>subagent.finish</c>, class Exec, trust agent). The arguments hash
/// covers the entry's identity and limits, never text a child wrote. An append that throws propagates, so the spawn is denied.
/// </summary>
public sealed class ToolCallLogSubagentTrace(IToolCallLog log) : ISubagentTrace
{
    /// <summary>The tool name written for a spawn row.</summary>
    public const string SpawnTool = "subagent.spawn";

    /// <summary>The tool name written for a finish row.</summary>
    public const string FinishTool = "subagent.finish";

    /// <inheritdoc />
    public async Task AppendAsync(SubagentTraceEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await log.AppendAsync(ToRecord(entry), cancellationToken).ConfigureAwait(false);
    }

    private static ToolCallRecord ToRecord(SubagentTraceEntry entry)
    {
        var spawned = entry.Kind == SubagentTraceKind.Spawned;
        return new ToolCallRecord(
            Guid.NewGuid(),
            entry.ParentId,
            spawned ? SpawnTool : FinishTool,
            nameof(ToolCapabilityClass.Exec),
            "agent",
            false,
            spawned ? ToolCallStatus.Succeeded : entry.Status switch
            {
                SubagentStatus.Completed => ToolCallStatus.Succeeded,
                SubagentStatus.Cancelled => ToolCallStatus.Unknown,
                _ => ToolCallStatus.Failed,
            },
            null,
            Hash(entry),
            entry.At,
            entry.At);
    }

    private static string Hash(SubagentTraceEntry e)
    {
        var text = string.Join(
            '\n',
            e.SubagentId,
            e.ChainId,
            e.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.ModelId,
            e.Worktree.ToString(),
            string.Join(',', e.Tools),
            string.Join(',', e.DeclaredPaths));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
