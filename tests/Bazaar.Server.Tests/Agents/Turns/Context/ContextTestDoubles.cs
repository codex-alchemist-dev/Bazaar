using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;

namespace Bazaar.Server.Tests.Agents.Turns.Context;

/// <summary>A source that returns a fixed list of items, for every turn.</summary>
internal sealed class FixedContextSource(params ContextSourceItem[] items) : IContextSource
{
    public Task<IReadOnlyList<ContextSourceItem>> ReadAsync(TurnContextRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContextSourceItem>>(items);
}

internal static class ContextTestData
{
    public static readonly Guid AgentId = Guid.Parse("0196a1d4-0000-7000-8000-000000000001");
    public static readonly Guid TurnId = Guid.Parse("0196a1d4-0000-7000-8000-000000000002");

    public static ContextSourceItem Item(ContextSection section, string itemId, string text, string version = "v1") =>
        new(itemId, version, text, section);

    /// <summary>
    /// Text that costs exactly <paramref name="tokens"/> under the chars-per-four estimate. The marker must fit in the
    /// space, so tests can search for it later.
    /// </summary>
    public static string Sized(string marker, int tokens)
    {
        if (marker.Length > tokens * 4)
        {
            throw new ArgumentException($"Marker '{marker}' does not fit in {tokens} tokens.", nameof(marker));
        }

        return marker.PadRight(tokens * 4, '.');
    }

    public static TurnContextRequest Request(string? triggerGcId = null, long? triggerGcSeq = null) =>
        new(AgentId, TurnId, TurnRole.Actor, "mention", [], triggerGcId, triggerGcSeq);

    public static TurnContextAssembler Assembler(ContextBudget budget, params ContextSourceItem[] items) =>
        new([new FixedContextSource(items)], budget);
}
