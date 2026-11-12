using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Gc.Wire;

/// <summary>
/// <c>gc.member.add</c>: invites a principal into a GC. The planner (<see cref="GcMembershipPlanner"/>) makes the policy
/// decision. The writer appends the event and the membership row together, and refuses the write if the head moved since
/// the plan was read; the handler then plans again from a fresh read.
/// </summary>
/// <remarks>
/// The invite policy comes from the <c>gc.invite_policy</c> setting (default: any active member may invite; always logged).
/// </remarks>
public sealed class GcMemberAddHandler(
    IGcLogWriter writer,
    GcReadAccess access,
    PgGcMembershipSource source,
    Bazaar.Server.Settings.ISettingStore settings) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcMemberAdd;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcMemberAdd>();
        if (request.Kind is not (PrincipalKind.Human or PrincipalKind.Agent))
            return WireReply.Fail(WireErrorCodes.BadRequest, "Only a human or an agent can be added.");
        if (request.Role is not ("member" or "observer"))
            return WireReply.Fail(WireErrorCodes.BadRequest, "Role must be member or observer.");

        var role = GcEnumText.ParseMemberRole(request.Role);
        var invitePolicy = await settings.GetAsync(GcSettings.InvitePolicyKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await GcMembershipWire.ChangeAsync(
            context, writer, access, source, request.GcId,
            (snapshot, viewer) => GcMembershipPlanner.PlanAddMember(
                snapshot.Gc, snapshot.Members, viewer, request.PrincipalId, request.Kind, role, snapshot.HeadSeq, invitePolicy),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>gc.member.remove</c>: removes a principal. The owner may remove anyone; a member may remove only themselves. The
/// planner makes that decision.
/// </summary>
public sealed class GcMemberRemoveHandler(IGcLogWriter writer, GcReadAccess access, PgGcMembershipSource source) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcMemberRemove;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcMemberRemove>();
        return await GcMembershipWire.ChangeAsync(
            context, writer, access, source, request.GcId,
            (snapshot, viewer) => GcMembershipPlanner.PlanRemoveMember(
                snapshot.Gc, snapshot.Members, viewer, request.PrincipalId, snapshot.HeadSeq),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The shared path for membership changes: access check, plan, append with the planned head, and a fresh plan on a stale head.</summary>
internal static class GcMembershipWire
{
    /// <summary>How many times a plan is retried when the head moves under it.</summary>
    private const int _maxAttempts = 3;

    public static async ValueTask<WireReply> ChangeAsync(
        WireRequestContext context,
        IGcLogWriter writer,
        GcReadAccess access,
        PgGcMembershipSource source,
        Guid gcId,
        Func<GcMembershipSnapshot, GcViewer, GcPlanResult> plan,
        CancellationToken ct)
    {
        var principal = context.Connection.Principal;
        var clientMsgId = context.Request.Id;
        if (string.IsNullOrEmpty(clientMsgId))
            return WireReply.Fail(WireErrorCodes.BadRequest, "A membership change needs a request id.");

        var viewer = GcWirePrincipals.PrincipalOf(principal);
        if (!await access.CanWriteAsync(gcId, viewer, ct).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);

        var actor = new GcViewer(principal.Id, viewer.Kind);
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var snapshot = await source.LoadAsync(gcId, ct).ConfigureAwait(false);
            if (snapshot is null)
                return WireReply.Fail(WireErrorCodes.NotFound);

            var planned = plan(snapshot, actor);
            if (!planned.Succeeded)
                return WireReply.Fail(WireErrorCodes.BadRequest, RefusalText(planned.Refusal!.Value));

            var ev = planned.Event!;
            var append = new AppendRequest(gcId, ev.Kind, AuthorKindOf(viewer.Kind), principal.Id, TrustOf(viewer.Kind), clientMsgId)
            {
                Membership = planned.Member is { } m
                    ? new MembershipChange(m.PrincipalId, GcEnumText.ToText(m.Role), m.LeftSeq is not null)
                    : null,
                PayloadJson = ev.PayloadJson,
                ExpectedHeadSeq = snapshot.HeadSeq,
            };

            try
            {
                var result = await writer.AppendAsync(append, ct).ConfigureAwait(false);
                return WireReply.Ok(context.Connection.Codec, new GcSendResult(result.Seq, result.Seq));
            }
            catch (GcStaleHeadException)
            {
                // The head moved: the decision was made against older membership, so plan again from a fresh read.
                continue;
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

        return WireReply.Fail(WireErrorCodes.BadRequest, "The GC changed while the change was planned. Try again.");
    }

    private static AuthorKind AuthorKindOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => AuthorKind.Owner,
        PrincipalKind.Agent => AuthorKind.Agent,
        _ => AuthorKind.Human,
    };

    private static SourceTrust TrustOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => SourceTrust.Owner,
        PrincipalKind.Agent => SourceTrust.Agent,
        _ => SourceTrust.MemberHuman,
    };

    private static string RefusalText(GcPlanRefusal reason) => reason switch
    {
        GcPlanRefusal.Archived => "The GC is archived.",
        GcPlanRefusal.NotPermitted => "Not permitted to make that change.",
        GcPlanRefusal.AlreadyMember => "That principal is already a member.",
        GcPlanRefusal.NoActiveMembership => "That principal is not a member.",
        GcPlanRefusal.DmFull => "This DM is full.",
        GcPlanRefusal.ThinkingMembershipFixed => "The membership of a Thinking GC is fixed.",
        _ => "The membership change was refused.",
    };
}
