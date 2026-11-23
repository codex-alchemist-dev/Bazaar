using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;

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
    IGcWriteAccess access,
    IGcMembershipSource source,
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
public sealed class GcMemberRemoveHandler(IGcLogWriter writer, IGcWriteAccess access, IGcMembershipSource source) : IWireRequestHandler
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
        IGcWriteAccess access,
        IGcMembershipSource source,
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

/// <summary>Setting <c>gc.default_gc_rule</c>: how <c>gc.list</c> picks <c>defaultGcId</c> (docs/design/02, GC list) [PROPOSED].</summary>
public enum GcDefaultRule
{
    /// <summary>The caller's most recently active GC that is not a DM, Thinking, ward or vent GC (the default).</summary>
    MostRecentlyActive,

    /// <summary>No default: <c>defaultGcId</c> is always null.</summary>
    None,
}

/// <summary>
/// <c>gc.list</c> [PROPOSED, docs/design/02, GC list]: the GCs the caller is an active member of, most recently active first, and the
/// caller's default GC. The list comes from membership rows only, for every principal including the owner, so it never names a GC
/// the caller is not in. Archived GCs are left out; Thinking GCs are left out unless asked for.
/// </summary>
public sealed class GcListHandler(IGcMemberListSource source, ISettingStore settings) : IWireRequestHandler
{
    /// <summary>Largest list a client may ask for.</summary>
    public const int MaxLimit = 200;

    // Private or recovery GCs are never an automatic landing place.
    private static readonly HashSet<string> _notDefaultKinds =
        [GcTypeKeys.Dm, GcTypeKeys.Thinking, GcTypeKeys.Ward, GcTypeKeys.Vent];

    /// <inheritdoc />
    public string Type => WireTypes.GcList;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcListRequest>();
        if (request.Limit < 1 || request.Limit > MaxLimit)
            return WireReply.Fail(WireErrorCodes.BadRequest, $"Limit must be between 1 and {MaxLimit}.");

        var caller = context.Connection.Principal;
        var rule = await settings.GetAsync(GcSettings.DefaultGcRuleKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        var memberships = await source.ListAsync(caller.Id, includeArchived: false, cancellationToken).ConfigureAwait(false);

        // The source is trusted for membership, but the handler still refuses anything archived and orders the result itself.
        var live = memberships
            .Where(m => !m.Archived)
            .OrderByDescending(m => m.LastActivityTs ?? DateTimeOffset.MinValue)
            .ThenBy(m => m.GcId)
            .ToList();

        Guid? defaultGc = rule == GcDefaultRule.MostRecentlyActive
            ? live.FirstOrDefault(m => !_notDefaultKinds.Contains(m.TypeKey))?.GcId
            : null;

        var listed = request.IncludeThinking ? live : live.Where(m => m.TypeKey != GcTypeKeys.Thinking).ToList();
        var items = listed
            .Take(request.Limit)
            .Select(m => new GcListItem(m.GcId, m.Title, m.TypeKey, m.MemberCount, m.HeadSeq, m.LastActivityTs))
            .ToList();

        return WireReply.Ok(context.Connection.Codec, new GcListReply(items, listed.Count > request.Limit, defaultGc));
    }
}
