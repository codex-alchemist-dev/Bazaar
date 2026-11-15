using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Edit;

/// <summary>An edit of a message the editor wrote, as a caller asks for it.</summary>
/// <param name="GcId">The GC the message is in.</param>
/// <param name="MessageId">The message to edit.</param>
/// <param name="EditorId">The principal asking for the edit.</param>
/// <param name="EditorKind">The kind of that principal.</param>
/// <param name="Text">The replacement text.</param>
/// <param name="ClientMsgId">The idempotency key of the edit event.</param>
public sealed record GcMessageEdit(
    Guid GcId,
    Guid MessageId,
    Guid EditorId,
    PrincipalKind EditorKind,
    string Text,
    string ClientMsgId);

/// <summary>Result of an edit: accepted with the new version and seq, refused with the reason, or not applicable.</summary>
public abstract record GcMessageEditOutcome;

/// <summary>The edit was written. <paramref name="Version"/> is the new version number; <paramref name="Seq"/> is the edit event's seq.</summary>
public sealed record GcMessageEditAccepted(int Version, long Seq) : GcMessageEditOutcome;

/// <summary>The edit was refused by the same policy a manual edit uses. Nothing was written.</summary>
public sealed record GcMessageEditRefused(string Reason) : GcMessageEditOutcome;

/// <summary>The GC or the message does not exist for this edit.</summary>
public sealed record GcMessageEditNotFound : GcMessageEditOutcome;

/// <summary>The current text can no longer be opened (its key was shredded), so the message cannot be edited.</summary>
public sealed record GcMessageEditUnopenable : GcMessageEditOutcome;

/// <summary>
/// The one way to edit a GC message. A manual edit and an import replace both go through it, so both are checked by
/// <c>MessageEditPlanner</c> (docs/design/01, Edits) and append the same rows.
/// </summary>
public interface IGcMessageEditor
{
    Task<GcMessageEditOutcome> EditAsync(GcMessageEdit request, CancellationToken ct);
}
