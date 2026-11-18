using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>
/// Merge rule for Constellation node positions (doc 52 section 5, item 4). Last writer wins per node
/// by HLC, and the previous position stays in history.
/// </summary>
public sealed class ConstellationPositionRule : LastWriterWinsRule
{
    /// <summary>Creates the rule over the given state.</summary>
    /// <param name="state">The last-writer-wins state.</param>
    public ConstellationPositionRule(ILastWriterWinsState state)
        : base(state)
    {
    }

    /// <inheritdoc />
    public override OfflineDataClass DataClass => OfflineDataClass.ConstellationPosition;

    /// <inheritdoc />
    protected override bool IsWellFormed(ReadOnlyMemory<byte> payload) =>
        RulePayloadCodec.ReadConstellationPosition(payload) is not null;
}
