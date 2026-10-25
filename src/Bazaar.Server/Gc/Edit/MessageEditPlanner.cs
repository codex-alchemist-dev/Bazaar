using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Edit;

/// <summary>Why an edit was refused. The planner returns one reason; it never throws for policy failures.</summary>
public enum MessageEditRefusal
{
    /// <summary>The editor is not the original author.</summary>
    NotAuthor,

    /// <summary>The GC is archived and therefore read-only.</summary>
    ArchivedGc,

    /// <summary>The new body is empty or whitespace only.</summary>
    EmptyBody,

    /// <summary>The new body exceeds the UTF-8 byte cap.</summary>
    BodyTooLarge,

    /// <summary>The edit window has closed for this message.</summary>
    OutsideEditWindow,

    /// <summary>The new body is identical to the current version, so nothing would change.</summary>
    UnchangedBody,
}

/// <summary>
/// Result of <see cref="MessageEditPlanner.Plan"/>: either a plan to persist or a refusal.
/// </summary>
public abstract record MessageEditResult;

/// <summary>The edit is allowed; the writer persists the plan.</summary>
/// <param name="Plan">Rows to append.</param>
public sealed record MessageEditAccepted(MessageEditPlan Plan) : MessageEditResult;

/// <summary>The edit is refused and nothing should be written.</summary>
/// <param name="Reason">Why the edit was refused.</param>
public sealed record MessageEditRefused(MessageEditRefusal Reason) : MessageEditResult;

/// <summary>
/// Rows that persisting an accepted edit appends. The writer (Gc/Write) assigns seq for every draft
/// and fills <see cref="MessageVersionRecord.Seq"/> with the seq of the <see cref="EditEvent"/>.
/// </summary>
/// <param name="NewVersion">The full copy of the new text. Seq is a placeholder 0 until the writer fills it.</param>
/// <param name="EditEvent">The <c>message_edit</c> event, with MessageId and Version set.</param>
/// <param name="SystemNote">The "source edited after use" note, only when software had already acted on the message.</param>
public sealed record MessageEditPlan(
    MessageVersionRecord NewVersion,
    GcEventDraft EditEvent,
    GcEventDraft? SystemNote);

/// <summary>
/// Pure edit policy for a GC message (docs/design/01-gc-data-model.md, Edits). Only the original
/// author may edit; every edit adds a full version and a <c>message_edit</c> event. No database access.
/// </summary>
public static class MessageEditPlanner
{
    /// <summary>Default for setting <c>gc.message_max_bytes</c>, in UTF-8 bytes.</summary>
    public const int DefaultMaxBodyBytes = 32768;

    /// <summary>
    /// Plans an edit. Checks run in this order: author, archive, empty body, body size, edit window,
    /// unchanged body.
    /// </summary>
    /// <param name="gc">The GC header, used for the archive check.</param>
    /// <param name="editor">Who asks to edit. Must be the original author by PrincipalId.</param>
    /// <param name="messageId">The message being edited.</param>
    /// <param name="originalAuthorId">Principal who wrote the original message.</param>
    /// <param name="currentVersion">Highest version number now stored (1 or more).</param>
    /// <param name="currentVersionSeq">Seq of the event that created the current version.</param>
    /// <param name="currentBody">Text of the current version, for the unchanged-body check.</param>
    /// <param name="originalCreatedAt">When the original message was written. The edit window is measured from here.</param>
    /// <param name="newBody">Replacement text.</param>
    /// <param name="now">Time of the edit.</param>
    /// <param name="editWindow">Edit window, or null for unlimited. The caller maps setting value 0 to null.</param>
    /// <param name="maxBodyBytes">Body cap in UTF-8 bytes (setting <c>gc.message_max_bytes</c>).</param>
    /// <param name="trust">Trust of the edit's content, set by the server at ingestion.</param>
    /// <param name="messageWasActedOn">True when software (a reaction or trigger) has already acted on the message.</param>
    public static MessageEditResult Plan(
        GcRecord gc,
        GcViewer editor,
        Guid messageId,
        Guid originalAuthorId,
        int currentVersion,
        long currentVersionSeq,
        string currentBody,
        DateTimeOffset originalCreatedAt,
        string newBody,
        DateTimeOffset now,
        TimeSpan? editWindow,
        int maxBodyBytes,
        SourceTrust trust,
        bool messageWasActedOn)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(currentBody);
        ArgumentNullException.ThrowIfNull(newBody);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBodyBytes, 1);

        if (editor.PrincipalId != originalAuthorId)
        {
            return new MessageEditRefused(MessageEditRefusal.NotAuthor);
        }

        if (gc.ArchivedAt is not null)
        {
            return new MessageEditRefused(MessageEditRefusal.ArchivedGc);
        }

        if (string.IsNullOrWhiteSpace(newBody))
        {
            return new MessageEditRefused(MessageEditRefusal.EmptyBody);
        }

        if (Encoding.UTF8.GetByteCount(newBody) > maxBodyBytes)
        {
            return new MessageEditRefused(MessageEditRefusal.BodyTooLarge);
        }

        if (editWindow is { } window && now - originalCreatedAt > window)
        {
            return new MessageEditRefused(MessageEditRefusal.OutsideEditWindow);
        }

        if (string.Equals(newBody, currentBody, StringComparison.Ordinal))
        {
            return new MessageEditRefused(MessageEditRefusal.UnchangedBody);
        }

        var nextVersion = currentVersion + 1;
        var authorKind = ToAuthorKind(editor.Kind);

        var newVersion = new MessageVersionRecord(
            gc.GcId,
            messageId,
            nextVersion,
            newBody,
            editor.PrincipalId,
            Seq: 0,
            now);

        var editEvent = new GcEventDraft(
            gc.GcId,
            GcEventKinds.MessageEdit,
            authorKind,
            editor.PrincipalId,
            trust,
            MessageId: messageId,
            Version: nextVersion);

        GcEventDraft? systemNote = messageWasActedOn
            ? new GcEventDraft(
                gc.GcId,
                GcEventKinds.System,
                AuthorKind.System,
                SystemAuthorId,
                SourceTrust.System,
                MessageId: messageId,
                TargetSeq: currentVersionSeq,
                PayloadJson: SystemNotePayloadJson)
            : null;

        return new MessageEditAccepted(new MessageEditPlan(newVersion, editEvent, systemNote));
    }

    /// <summary>Payload of the "source edited after use" note.</summary>
    public const string SystemNotePayloadJson = "{\"note\":\"source edited after use\"}";

    /// <summary>
    /// Author id for server-written rows. It is the empty GUID until a server principal id exists;
    /// the writer may replace it. Recorded as a decision in the G4 report.
    /// </summary>
    public static readonly Guid SystemAuthorId = Guid.Empty;

    private static AuthorKind ToAuthorKind(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => AuthorKind.Owner,
        PrincipalKind.Human => AuthorKind.Human,
        PrincipalKind.Agent => AuthorKind.Agent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown principal kind."),
    };
}
