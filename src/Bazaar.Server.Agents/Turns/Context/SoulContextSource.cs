namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>
/// The seam for an agent's charter text. The charter is a Vault file: <c>soul_versions</c> keeps its SHA-256 and the
/// preferences JSON, never the text (doc 07). No reader for the Vault charter exists yet, so the default returns null and the
/// System section stays empty. Set a real source here when the Vault reader and the agent-to-soul mapping land.
/// </summary>
public interface ISoulCharterSource
{
    /// <summary>The charter text for the agent, or null when none is available.</summary>
    ValueTask<string?> ReadCharterAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>The default charter source: no text. Documented seam, not a missing feature.</summary>
public sealed class NullSoulCharterSource : ISoulCharterSource
{
    /// <inheritdoc />
    public ValueTask<string?> ReadCharterAsync(Guid agentId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
}

/// <summary>Puts the agent's charter into the System section, when a charter source returns text.</summary>
public sealed class SoulContextSource(ISoulCharterSource charters) : IContextSource
{
    private readonly ISoulCharterSource _charters = charters ?? throw new ArgumentNullException(nameof(charters));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextSourceItem>> ReadAsync(TurnContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = await _charters.ReadCharterAsync(request.AgentId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text)
            ? []
            : [new ContextSourceItem(request.AgentId.ToString("D"), "charter", text, ContextSection.System)];
    }
}
