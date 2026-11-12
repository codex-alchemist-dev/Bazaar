using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Gc.Wire;

/// <summary>
/// <c>gc.edit</c>: replaces the text of a message the sender wrote. The sender must be the original author and must still
/// be able to write to the GC. The planner (<see cref="MessageEditPlanner"/>) makes every policy decision; this handler reads
/// the state, opens the current version, seals the new text under the author's key, and appends the event and any note.
/// </summary>
/// <remarks>
/// Not yet wired: the edit window (unlimited here, because no <c>gc.edit_window</c> setting exists yet) and the
/// "messageWasActedOn" input (always false, because reaction and trigger tracking does not record acted-on messages yet).
/// Both are documented in the PR so the defaults are visible.
/// </remarks>
public sealed class GcEditHandler(
    IGcLogWriter writer,
    IGcBodyCodec bodies,
    GcReadAccess access,
    PgGcEditSource source,
    TimeProvider? timeProvider = null) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcEdit;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcEdit>();
        var principal = context.Connection.Principal;
        var clientMsgId = context.Request.Id;

        if (string.IsNullOrWhiteSpace(request.Text))
            return WireReply.Fail(WireErrorCodes.BadRequest, "Message text is empty.");
        if (request.Text.Length > GcSendHandler.MaxTextLength)
            return WireReply.Fail(WireErrorCodes.BadRequest, "Message text is too long.");
        if (string.IsNullOrEmpty(clientMsgId))
            return WireReply.Fail(WireErrorCodes.BadRequest, "An edit needs a request id.");

        var viewer = GcWirePrincipals.PrincipalOf(principal);
        if (!await access.CanWriteAsync(request.GcId, viewer, cancellationToken).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);

        var snapshot = await source.LoadAsync(request.GcId, request.MessageId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return WireReply.Fail(WireErrorCodes.NotFound);

        // The current text is needed for the unchanged-body check. A version whose key was shredded cannot be opened, so the
        // message can no longer be edited (the author's key is gone; a new key would not decrypt the old text).
        var currentBody = await bodies.OpenAsync(snapshot.BodyCiphertext, snapshot.KeyId, snapshot.Nonce, cancellationToken).ConfigureAwait(false);
        if (currentBody is null)
            return WireReply.Fail(WireErrorCodes.BadRequest, "This message can no longer be edited.");

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var plan = MessageEditPlanner.Plan(
            snapshot.Gc,
            new GcViewer(principal.Id, viewer.Kind),
            request.MessageId,
            snapshot.AuthorId,
            snapshot.Version,
            snapshot.VersionSeq,
            currentBody,
            snapshot.CreatedAt,
            request.Text,
            now,
            editWindow: null,
            MessageEditPlanner.DefaultMaxBodyBytes,
            viewer.Kind == PrincipalKind.Agent ? SourceTrust.Agent : viewer.Kind == PrincipalKind.Owner ? SourceTrust.Owner : SourceTrust.MemberHuman,
            messageWasActedOn: false);

        if (plan is MessageEditRefused refused)
            return WireReply.Fail(WireErrorCodes.BadRequest, RefusalText(refused.Reason));

        var accepted = ((MessageEditAccepted)plan).Plan;
        var sealed_ = await bodies.SealAsync(principal.Id, request.Text, cancellationToken).ConfigureAwait(false);
        var appends = new List<AppendRequest>
        {
            new(
                request.GcId,
                GcEventKinds.MessageEdit,
                AuthorKindOf(viewer.Kind),
                principal.Id,
                accepted.EditEvent.SourceTrust,
                clientMsgId)
            {
                MessageId = request.MessageId,
                Version = accepted.NewVersion.Version,
                Message = sealed_,
            },
        };

        if (accepted.SystemNote is { } note)
        {
            // The note is written in the same transaction and keyed off the edit's request id, so a retry does not add it twice.
            appends.Add(new AppendRequest(
                request.GcId,
                GcEventKinds.System,
                AuthorKind.System,
                MessageEditPlanner.SystemAuthorId,
                SourceTrust.System,
                clientMsgId + ":note")
            {
                TargetSeq = note.TargetSeq,
                PayloadJson = note.PayloadJson,
            });
        }

        try
        {
            var results = await writer.AppendManyAsync(appends, cancellationToken).ConfigureAwait(false);
            return WireReply.Ok(context.Connection.Codec, new GcSendResult(results[0].Seq, accepted.NewVersion.Version));
        }
        catch (GcNotFoundException)
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }
        catch (GcArchivedException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The GC is archived.");
        }
    }

    private static AuthorKind AuthorKindOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => AuthorKind.Owner,
        PrincipalKind.Agent => AuthorKind.Agent,
        _ => AuthorKind.Human,
    };

    private static string RefusalText(MessageEditRefusal reason) => reason switch
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
