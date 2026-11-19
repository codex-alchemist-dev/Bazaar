namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>
/// One item a context source returned. <see cref="ItemId"/> and <see cref="Version"/> name the source row, and
/// <see cref="Text"/> is the content the model sees. For <see cref="ContextSection.GcBackread"/> items, <see cref="ItemId"/>
/// is the GC id, so the assembler can find the trigger thread. The manifest never records <see cref="Text"/>.
/// </summary>
public sealed record ContextSourceItem(string ItemId, string Version, string Text, ContextSection Section);

/// <summary>
/// Supplies the items for a turn's context. The assembler calls every registered source once per turn, in registration
/// order, and groups the items by <see cref="ContextSourceItem.Section"/>.
/// </summary>
// TODO(component): bind to 08/01/12 read APIs when those land (Memory and Vault from 08, GcBackread and Inbox from 01,
// TaskState from 12).
public interface IContextSource
{
    Task<IReadOnlyList<ContextSourceItem>> ReadAsync(TurnContextRequest request, CancellationToken cancellationToken);
}
