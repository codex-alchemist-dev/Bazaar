using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Gc.Edit;

/// <summary>
/// The edit path shared by a manual edit (<c>gc.edit</c>) and an import replace: reads the message, lets
/// <see cref="MessageEditPlanner"/> decide, seals the new text under the editor's key, and appends the edit and any note.
/// The caller checks that the editor may write to the GC before calling this.
/// </summary>
public sealed class GcMessageEditor(
    IGcLogWriter writer,
    IGcBodyCodec bodies,
    PgGcEditSource source,
    ISettingStore settings,
    TimeProvider? timeProvider = null) : IGcMessageEditor
{
    /// <summary>
    /// A single edit: plans it and appends its rows in one transaction. Used by the manual edit handler.
    /// </summary>
    public async Task<GcMessageEditOutcome> EditAsync(GcMessageEdit request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await PlanAsync([request], ct).ConfigureAwait(false);
        if (plan is GcMessageEditPlanRefused refused)
            return refused.Outcome;

        var rows = ((GcMessageEditPlanned)plan).Rows;
        try
        {
            var results = await writer.AppendManyAsync(rows, ct).ConfigureAwait(false);
            return new GcMessageEditAccepted(((GcMessageEditPlanned)plan).Versions[0], results[0].Seq);
        }
        catch (GcNotFoundException)
        {
            return new GcMessageEditNotFound();
        }
        catch (GcArchivedException)
        {
            return new GcMessageEditRefused("The GC is archived.");
        }
    }

    /// <inheritdoc />
    public async Task<GcMessageEditPlanResult> PlanAsync(IReadOnlyList<GcMessageEdit> edits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var seen = new HashSet<Guid>();
        var rows = new List<AppendRequest>();
        var versions = new List<int>();
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (!seen.Add(edit.MessageId))
                return new GcMessageEditPlanRefused(i, new GcMessageEditRefused("Two edits of one message in a batch."));

            var planned = await PlanOneAsync(edit, ct).ConfigureAwait(false);
            if (planned.Refusal is { } refusal)
                return new GcMessageEditPlanRefused(i, refusal);
            rows.AddRange(planned.Rows);
            versions.Add(planned.Version);
        }
        return new GcMessageEditPlanned(rows, versions);
    }

    private async Task<(GcMessageEditOutcome? Refusal, IReadOnlyList<AppendRequest> Rows, int Version)> PlanOneAsync(GcMessageEdit request, CancellationToken ct)
    {
        var snapshot = await source.LoadAsync(request.GcId, request.MessageId, ct).ConfigureAwait(false);
        if (snapshot is null)
            return (new GcMessageEditNotFound(), [], 0);

        // The current text is needed for the unchanged-body check. A version whose key was shredded cannot be opened, so the
        // message can no longer be edited (the author's key is gone; a new key would not decrypt the old text).
        var currentBody = await bodies.OpenAsync(snapshot.BodyCiphertext, snapshot.KeyId, snapshot.Nonce, ct).ConfigureAwait(false);
        if (currentBody is null)
            return (new GcMessageEditUnopenable(), [], 0);

        var editWindowHours = await settings.GetAsync(GcSettings.EditWindowHoursKey, cancellationToken: ct).ConfigureAwait(false);
        var editWindow = editWindowHours == 0 ? (TimeSpan?)null : TimeSpan.FromHours(editWindowHours);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var plan = MessageEditPlanner.Plan(
            snapshot.Gc,
            new GcViewer(request.EditorId, request.EditorKind),
            request.MessageId,
            snapshot.AuthorId,
            snapshot.Version,
            snapshot.VersionSeq,
            currentBody,
            snapshot.CreatedAt,
            request.Text,
            now,
            editWindow,
            MessageEditPlanner.DefaultMaxBodyBytes,
            request.EditorKind == GcParticipantKind.Agent ? SourceTrust.Agent : request.EditorKind == GcParticipantKind.Owner ? SourceTrust.Owner : SourceTrust.MemberHuman,
            messageWasActedOn: false);

        if (plan is MessageEditRefused refused)
            return (new GcMessageEditRefused(RefusalText(refused.Reason)), [], 0);

        var accepted = ((MessageEditAccepted)plan).Plan;
        var sealedBody = await bodies.SealAsync(request.EditorId, request.Text, ct).ConfigureAwait(false);
        var rows = new List<AppendRequest>
        {
            new(
                request.GcId,
                GcEventKinds.MessageEdit,
                AuthorKindOf(request.EditorKind),
                request.EditorId,
                accepted.EditEvent.SourceTrust,
                request.ClientMsgId)
            {
                MessageId = request.MessageId,
                Version = accepted.NewVersion.Version,
                Message = sealedBody,
            },
        };

        if (accepted.SystemNote is { } note)
        {
            // The note is written in the same transaction and keyed off the edit's request id, so a retry does not add it twice.
            rows.Add(new AppendRequest(
                request.GcId,
                GcEventKinds.System,
                AuthorKind.System,
                MessageEditPlanner.SystemAuthorId,
                SourceTrust.System,
                request.ClientMsgId + ":note")
            {
                TargetSeq = note.TargetSeq,
                PayloadJson = note.PayloadJson,
            });
        }
        return (null, rows, accepted.NewVersion.Version);
    }

    private static AuthorKind AuthorKindOf(GcParticipantKind kind) => kind switch
    {
        GcParticipantKind.Owner => AuthorKind.Owner,
        GcParticipantKind.Agent => AuthorKind.Agent,
        _ => AuthorKind.Human,
    };

    internal static string RefusalText(MessageEditRefusal reason) => reason switch
    {
        MessageEditRefusal.NotAuthor => "Only the author can edit this message.",
        MessageEditRefusal.ArchivedGc => "The GC is archived.",
        MessageEditRefusal.EmptyBody => "Message text is empty.",
        MessageEditRefusal.BodyTooLarge => "Message text is too long.",
        MessageEditRefusal.OutsideEditWindow => "The edit window has closed.",
        MessageEditRefusal.UnchangedBody => "The text is unchanged.",
        _ => "The edit was refused.",
    };
}
