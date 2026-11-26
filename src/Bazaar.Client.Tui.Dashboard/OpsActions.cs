using System.Globalization;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>The write actions of the resident TUI (doc 58 sections 3 and 5).</summary>
public enum OpsActionKind
{
    StartRebuild,
    CommitSelection,
    RevokeMember,
    RestoreMember,
    RevokeDevice,
    Approve,
    Deny,
}

public enum OpsActionStatus
{
    /// <summary>The server did it.</summary>
    Done,

    /// <summary>The operator said no, or there was nothing to do. Nothing was written.</summary>
    Cancelled,

    /// <summary>The account lacks the capability the web admin panel asks for the same action. Audited as denied.</summary>
    Forbidden,

    /// <summary>The session ended (sliding or absolute expiry, revoked device) before the action.</summary>
    SessionEnded,

    /// <summary>A port is not wired or did not answer. Nothing was changed.</summary>
    Unavailable,

    /// <summary>The server's 409: a stale selection revision, or a rebuild already running.</summary>
    Conflict,

    /// <summary>Refused before or by the server: invalid selection, no step-up, the audit entry could not be appended, or the port said no.</summary>
    Refused,

    /// <summary>The call threw. The message is generic, because an exception text can carry anything.</summary>
    Failed,
}

/// <summary>What happened, in one plain line fit to show on the status row. Never carries a code, a proof or a secret.</summary>
public sealed record OpsActionOutcome(OpsActionStatus Status, string Message)
{
    public bool Succeeded => Status == OpsActionStatus.Done;
}

/// <summary>The write ports. Any may be null: the action then reports <see cref="OpsActionStatus.Unavailable"/> (or, for a missing step-up, refuses to approve).</summary>
public sealed record OpsWritePorts(
    IAdminModuleSelection? Modules = null,
    IOpsMemberRevoker? Members = null,
    IOpsDeviceRevoker? Devices = null,
    IOpsApprovalDecider? Approvals = null,
    IOpsStepUp? StepUp = null,
    IOpsMemberRestorer? Restorer = null);

/// <summary>
/// The Ops write actions (doc 58 sections 3 and 5): start a rebuild, commit a module selection, revoke a person or a device, approve or
/// refuse an approval. The TUI adds no privilege, so each action runs the same chain as the web panel (doc 51 sections 2 and 5):
/// <list type="number">
/// <item>the session is still live in the store, so revocation ends it at once;</item>
/// <item>the principal is human and holds the capability the panel asks for that area at instance scope (doc 48 section 4), else
/// <c>admin.web.denied</c> is audited and nothing happens;</item>
/// <item>a confirm prompt that defaults to No;</item>
/// <item>for an approval, the step-up of doc 32 section 5a through <see cref="IOpsStepUp"/>, failing closed when none is wired;</item>
/// <item>the audit entry, written first and fail closed: when it cannot be appended, the action is refused and the port is not called;</item>
/// <item>the port call. A 409 (stale revision, rebuild already running) is reported, not retried.</item>
/// </list>
/// A rebuild keeps one idempotency key across retries until the server gives an answer, so a call that threw can be repeated safely.
/// The step-up proof and any admin code are never drawn, logged or audited.
/// </summary>
public sealed class OpsActions(
    OpsPorts reads,
    OpsWritePorts writes,
    IPermissionChecker? permissions,
    IAdminAudit audit,
    OpsSignIn signIn,
    IPromptWidgets widgets)
{
    private const int _messageWidth = 100;

    private string? _rebuildKey;

    /// <summary>The capabilities the web panel's gate asks for the same area (<c>AdminGate.CapabilitiesFor</c>); approvals need the approval capability.</summary>
    public static IReadOnlyList<Capability> CapabilitiesFor(OpsActionKind kind) => kind switch
    {
        OpsActionKind.StartRebuild or OpsActionKind.CommitSelection => [Capabilities.AdminModules],
        OpsActionKind.RevokeMember or OpsActionKind.RestoreMember => [Capabilities.AdminMembers],
        OpsActionKind.RevokeDevice => [Capabilities.AdminDevices],
        OpsActionKind.Approve or OpsActionKind.Deny => [Capabilities.Approve],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The key that starts an action on a screen, or null. Letters avoid the menu shortcuts of <see cref="DefaultMenus"/>.</summary>
    public static OpsActionKind? KeyFor(string screenId, char key) => (screenId, char.ToLowerInvariant(key)) switch
    {
        (DefaultMenus.Modules, 'c') => OpsActionKind.CommitSelection,
        (DefaultMenus.Modules or DefaultMenus.Rebuilds, 'b') => OpsActionKind.StartRebuild,
        (DefaultMenus.Users, 'x') => OpsActionKind.RevokeMember,
        (DefaultMenus.Users, 's') => OpsActionKind.RestoreMember,
        (DefaultMenus.Users, 'v') => OpsActionKind.RevokeDevice,
        (DefaultMenus.Approvals, 'y') => OpsActionKind.Approve,
        (DefaultMenus.Approvals, 'n') => OpsActionKind.Deny,
        _ => null,
    };

    /// <summary>The key hint for a screen, or null when it has no actions.</summary>
    public static string? HintFor(string screenId) => screenId switch
    {
        DefaultMenus.Modules => "c: save selection  b: start rebuild",
        DefaultMenus.Rebuilds => "b: start rebuild",
        DefaultMenus.Users => "x: suspend person  s: unsuspend  v: revoke device",
        DefaultMenus.Approvals => "y: approve  n: refuse",
        _ => null,
    };

    /// <summary>Runs one action with its pickers (the operator chooses the subject from the live lists).</summary>
    public Task<OpsActionOutcome> RunAsync(OpsSession session, OpsActionKind kind, CancellationToken ct = default) => kind switch
    {
        OpsActionKind.StartRebuild => StartRebuildAsync(session, ct),
        OpsActionKind.CommitSelection => PickAndCommitSelectionAsync(session, ct),
        OpsActionKind.RevokeMember => PickAndRevokeMemberAsync(session, ct),
        OpsActionKind.RestoreMember => PickAndRestoreMemberAsync(session, ct),
        OpsActionKind.RevokeDevice => PickAndRevokeDeviceAsync(session, ct),
        OpsActionKind.Approve => PickAndDecideAsync(session, approve: true, ct),
        OpsActionKind.Deny => PickAndDecideAsync(session, approve: false, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ---- Rebuild -------------------------------------------------------------------------------------------------------------

    public async Task<OpsActionOutcome> StartRebuildAsync(OpsSession session, CancellationToken ct = default)
    {
        if (writes.Modules is not { } modules)
        {
            return Unavailable("Rebuilds");
        }

        if (await GateAsync(session, OpsActionKind.StartRebuild, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        if (!Confirm("Start a rebuild of the company clients now? A rebuild publishes the selected modules to every client."))
        {
            return Cancelled();
        }

        // One key until the server answers, so repeating after a throw cannot start a second build.
        var key = _rebuildKey ??= "tui-" + Guid.NewGuid().ToString("N");
        if (!await TryAuditAsync(session, AdminAuditKinds.RebuildStart, ("idempotencyKey", key)).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        AdminRebuildStart result;
        try
        {
            result = await modules.StartRebuildAsync(session.Identity.PrincipalId, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("The rebuild request did not finish. Repeating it is safe.");
        }

        _rebuildKey = null;
        if (result.Started)
        {
            return Done("Rebuild started" + (string.IsNullOrWhiteSpace(result.BuildId) ? "." : $" (build {TextFit.Clean(result.BuildId)})."));
        }

        return new OpsActionOutcome(
            OpsActionStatus.Conflict,
            "No rebuild started: " + (string.IsNullOrWhiteSpace(result.Reason) ? "one is already running." : TextFit.Cut(result.Reason, _messageWidth)));
    }

    // ---- Module selection ----------------------------------------------------------------------------------------------------

    private async Task<OpsActionOutcome> PickAndCommitSelectionAsync(OpsSession session, CancellationToken ct)
    {
        if (writes.Modules is not { } modules)
        {
            return Unavailable("Module selection");
        }

        if (await GateAsync(session, OpsActionKind.CommitSelection, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        AdminModuleCatalogue catalogue;
        try
        {
            catalogue = await modules.GetCatalogueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("The module list could not be read.");
        }

        static string Label(AdminModuleInfo m) => $"{TextFit.Clean(m.Name)} {TextFit.Clean(m.Version)} ({TextFit.Clean(m.Id)})";
        var labels = catalogue.Modules.ToDictionary(Label, m => m.Id);
        var picked = widgets.PickMany(
            "Modules to include",
            [.. labels.Keys],
            [.. catalogue.Modules.Where(m => m.Selected).Select(Label)]);
        if (picked is null)
        {
            return Cancelled();
        }

        // The revision is the one the operator saw when picking: a change made meanwhile gives the server's 409.
        return await CommitSelectionAsync(session, [.. picked.Select(label => labels[label])], catalogue.Revision, ct).ConfigureAwait(false);
    }

    /// <summary>Dry run, confirm, audit, then commit against <paramref name="expectedRevision"/>. A stale revision is a conflict, never retried.</summary>
    public async Task<OpsActionOutcome> CommitSelectionAsync(
        OpsSession session, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModuleIds);
        if (writes.Modules is not { } modules)
        {
            return Unavailable("Module selection");
        }

        if (await GateAsync(session, OpsActionKind.CommitSelection, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        AdminSelectionPreview preview;
        try
        {
            preview = await modules.PreviewAsync(selectedModuleIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("The selection could not be checked.");
        }

        if (!preview.Valid)
        {
            return new OpsActionOutcome(OpsActionStatus.Refused, "Selection not valid: " + Problems(preview.Problems));
        }

        if (preview.Added.Count == 0 && preview.Removed.Count == 0)
        {
            return new OpsActionOutcome(OpsActionStatus.Cancelled, "The selection is unchanged.");
        }

        var change = $"add {preview.Added.Count}, remove {preview.Removed.Count}";
        if (!Confirm($"Save this module selection ({change})? It takes effect at the next rebuild."))
        {
            return Cancelled();
        }

        if (!await TryAuditAsync(
                session,
                AdminAuditKinds.SelectionCommit,
                ("expectedRevision", expectedRevision.ToString(CultureInfo.InvariantCulture)),
                ("selected", string.Join(",", selectedModuleIds))).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        AdminSelectionCommit result;
        try
        {
            result = await modules.CommitAsync(session.Identity.PrincipalId, selectedModuleIds, expectedRevision, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("The selection was not saved. Check the Modules screen before trying again.");
        }

        if (result.Committed)
        {
            return Done($"Selection saved (revision {result.Revision.ToString(CultureInfo.InvariantCulture)}). Start a rebuild to apply it.");
        }

        return result.Problems.Count > 0
            ? new OpsActionOutcome(OpsActionStatus.Refused, "Selection not saved: " + Problems(result.Problems))
            : new OpsActionOutcome(
                OpsActionStatus.Conflict,
                $"The selection changed while you were editing (now revision {result.Revision.ToString(CultureInfo.InvariantCulture)}). Reload the Modules screen and try again.");
    }

    // ---- Revocation ----------------------------------------------------------------------------------------------------------

    private async Task<OpsActionOutcome> PickAndRevokeMemberAsync(OpsSession session, CancellationToken ct)
    {
        if (reads.Users is null)
        {
            return Unavailable("The user list");
        }

        if (await GateAsync(session, OpsActionKind.RevokeMember, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var users = await SoftPort.TryAsync(reads.Users, (source, token) => source.ListAsync(token), ct).ConfigureAwait(false);
        if (users is null)
        {
            return Unavailable("The user list");
        }

        var live = users.Where(u => !u.Revoked).ToDictionary(u => $"{TextFit.Clean(u.DisplayName)} | {TextFit.Clean(u.Role)} | {u.PrincipalId:D}");
        if (live.Count == 0)
        {
            return new OpsActionOutcome(OpsActionStatus.Cancelled, "Nobody is left to suspend.");
        }

        var pick = widgets.PickOne("Suspend which person", [.. live.Keys], null);
        return pick is null
            ? Cancelled()
            : await RevokeMemberAsync(session, live[pick].PrincipalId, live[pick].DisplayName, ct).ConfigureAwait(false);
    }

    public async Task<OpsActionOutcome> RevokeMemberAsync(OpsSession session, Guid principalId, string? displayName, CancellationToken ct = default)
    {
        if (writes.Members is not { } members)
        {
            return Unavailable("Revoking people");
        }

        if (await GateAsync(session, OpsActionKind.RevokeMember, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var who = string.IsNullOrWhiteSpace(displayName) ? principalId.ToString("D") : TextFit.Cut(displayName, 40);
        if (!Confirm($"Suspend {who}? Their devices and approval keys are revoked at once and they cannot sign in. Unsuspending later does not bring the devices back."))
        {
            return Cancelled();
        }

        if (!await TryAuditAsync(session, OpsAuditKinds.MemberRevoke, ("principal", principalId.ToString("D"))).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        return await WriteAsync(
            () => members.RevokeMemberAsync(session.Identity.PrincipalId, principalId, ct),
            $"Suspended {who}.",
            "Suspending",
            "The suspension did not finish. Check the Users screen.").ConfigureAwait(false);
    }

    private async Task<OpsActionOutcome> PickAndRestoreMemberAsync(OpsSession session, CancellationToken ct)
    {
        if (reads.Users is null)
        {
            return Unavailable("The user list");
        }

        if (await GateAsync(session, OpsActionKind.RestoreMember, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var users = await SoftPort.TryAsync(reads.Users, (source, token) => source.ListAsync(token), ct).ConfigureAwait(false);
        if (users is null)
        {
            return Unavailable("The user list");
        }

        // The list marks suspended and removed people alike as revoked; the identity module refuses to restore a removed one.
        var revoked = users.Where(u => u.Revoked).ToDictionary(u => $"{TextFit.Clean(u.DisplayName)} | {TextFit.Clean(u.Role)} | {u.PrincipalId:D}");
        if (revoked.Count == 0)
        {
            return new OpsActionOutcome(OpsActionStatus.Cancelled, "Nobody is suspended.");
        }

        var pick = widgets.PickOne("Unsuspend which person", [.. revoked.Keys], null);
        return pick is null
            ? Cancelled()
            : await RestoreMemberAsync(session, revoked[pick].PrincipalId, revoked[pick].DisplayName, ct).ConfigureAwait(false);
    }

    public async Task<OpsActionOutcome> RestoreMemberAsync(OpsSession session, Guid principalId, string? displayName, CancellationToken ct = default)
    {
        if (writes.Restorer is not { } restorer)
        {
            return Unavailable("Unsuspending people");
        }

        if (await GateAsync(session, OpsActionKind.RestoreMember, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var who = string.IsNullOrWhiteSpace(displayName) ? principalId.ToString("D") : TextFit.Cut(displayName, 40);
        if (!Confirm($"Unsuspend {who}? They can sign in again, and must pair their devices again."))
        {
            return Cancelled();
        }

        if (!await TryAuditAsync(session, OpsAuditKinds.MemberRestore, ("principal", principalId.ToString("D"))).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        return await WriteAsync(
            () => restorer.RestoreMemberAsync(session.Identity.PrincipalId, principalId, ct),
            $"Unsuspended {who}.",
            "Unsuspending",
            "The change did not finish. Check the Users screen.").ConfigureAwait(false);
    }

    private async Task<OpsActionOutcome> PickAndRevokeDeviceAsync(OpsSession session, CancellationToken ct)
    {
        if (reads.Devices is null)
        {
            return Unavailable("The device list");
        }

        if (await GateAsync(session, OpsActionKind.RevokeDevice, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var devices = await SoftPort.TryAsync(reads.Devices, (source, token) => source.ListAsync(token), ct).ConfigureAwait(false);
        if (devices is null)
        {
            return Unavailable("The device list");
        }

        var live = devices.Where(d => !d.Revoked).ToDictionary(d => $"{TextFit.Clean(d.Label)} | {d.DeviceId:D}");
        if (live.Count == 0)
        {
            return new OpsActionOutcome(OpsActionStatus.Cancelled, "No active device is left to revoke.");
        }

        var pick = widgets.PickOne("Revoke which device", [.. live.Keys], null);
        return pick is null
            ? Cancelled()
            : await RevokeDeviceAsync(session, live[pick].DeviceId, live[pick].Label, ct).ConfigureAwait(false);
    }

    public async Task<OpsActionOutcome> RevokeDeviceAsync(OpsSession session, Guid deviceId, string? label, CancellationToken ct = default)
    {
        if (writes.Devices is not { } devices)
        {
            return Unavailable("Revoking devices");
        }

        if (await GateAsync(session, OpsActionKind.RevokeDevice, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var what = string.IsNullOrWhiteSpace(label) ? deviceId.ToString("D") : TextFit.Cut(label, 40);
        if (!Confirm($"Revoke the device {what}? It stops working at once and its approval key is revoked with it."))
        {
            return Cancelled();
        }

        if (!await TryAuditAsync(session, OpsAuditKinds.DeviceRevoke, ("device", deviceId.ToString("D"))).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        return await WriteAsync(
            () => devices.RevokeDeviceAsync(session.Identity.PrincipalId, deviceId, ct),
            $"Revoked device {what}.",
            "Revoking",
            "The revocation did not finish. Check the Users screen.").ConfigureAwait(false);
    }

    // ---- Approvals -----------------------------------------------------------------------------------------------------------

    private async Task<OpsActionOutcome> PickAndDecideAsync(OpsSession session, bool approve, CancellationToken ct)
    {
        var kind = approve ? OpsActionKind.Approve : OpsActionKind.Deny;
        if (approve && writes.StepUp is null)
        {
            return ApproveFromOwnerDevice();
        }

        if (reads.Approvals is null)
        {
            return Unavailable("The approvals list");
        }

        if (await GateAsync(session, kind, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var pending = await SoftPort.TryAsync(reads.Approvals, (source, token) => source.ListPendingAsync(token), ct).ConfigureAwait(false);
        if (pending is null)
        {
            return Unavailable("The approvals list");
        }

        if (pending.Count == 0)
        {
            return new OpsActionOutcome(OpsActionStatus.Cancelled, "Nothing is waiting.");
        }

        var byLabel = pending.ToDictionary(a => $"{TextFit.Clean(a.Action)} (asked by {TextFit.Clean(a.Requester)}) | {TextFit.Clean(a.Id)}");
        var pick = widgets.PickOne(approve ? "Approve which request" : "Refuse which request", [.. byLabel.Keys], null);
        return pick is null
            ? Cancelled()
            : await DecideApprovalAsync(session, byLabel[pick].Id, approve, byLabel[pick].Action, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Approving needs the step-up (a signed owner approval, doc 32 section 5a); with none wired it is refused and the operator is sent to
    /// a client. Refusing a request needs no step-up. Both need the approval capability and an audit entry.
    /// </summary>
    public async Task<OpsActionOutcome> DecideApprovalAsync(
        OpsSession session, string approvalId, bool approve, string? actionSummary, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        if (approve && writes.StepUp is null)
        {
            return ApproveFromOwnerDevice();
        }

        if (writes.Approvals is not { } approvals)
        {
            return Unavailable("Approvals");
        }

        var kind = approve ? OpsActionKind.Approve : OpsActionKind.Deny;
        if (await GateAsync(session, kind, ct).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var what = string.IsNullOrWhiteSpace(actionSummary) ? TextFit.Clean(approvalId) : TextFit.Cut(actionSummary, 60);
        if (!Confirm(approve ? $"Approve \"{what}\"? This lets the action go ahead." : $"Refuse \"{what}\"?"))
        {
            return Cancelled();
        }

        string? proof = null;
        if (approve)
        {
            if (writes.StepUp is not { } stepUp)
            {
                return new OpsActionOutcome(OpsActionStatus.Refused, "Approving needs a signed approval from a device with an approval key. Approve it from a client.");
            }

            OpsStepUpResult step;
            try
            {
                step = await stepUp.RequestAsync(new OpsStepUpRequest(session.Identity.PrincipalId, "approval.approve", approvalId), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new OpsActionOutcome(OpsActionStatus.Refused, "The signed approval could not be obtained. Nothing was approved.");
            }

            if (!step.Granted || string.IsNullOrEmpty(step.Proof))
            {
                var why = string.IsNullOrWhiteSpace(step.Reason) ? string.Empty : ": " + TextFit.Cut(step.Reason, 60);
                return new OpsActionOutcome(OpsActionStatus.Refused, "The signed approval was not given" + why + ". Nothing was approved.");
            }

            proof = step.Proof;
        }

        // The proof is never an audit field; the entry says only that a step-up was given.
        if (!await TryAuditAsync(
                session,
                OpsAuditKinds.ApprovalDecide,
                ("approvalId", approvalId),
                ("decision", approve ? "approve" : "deny"),
                ("stepUp", approve ? "granted" : "not-required")).ConfigureAwait(false))
        {
            return AuditRefused();
        }

        return await WriteAsync(
            () => approvals.DecideAsync(session.Identity.PrincipalId, approvalId, approve, proof, ct),
            approve ? "Approved." : "Refused.",
            approve ? "Approving" : "Refusing",
            "The decision did not finish. Check the Approvals screen.").ConfigureAwait(false);
    }

    // ---- Shared chain --------------------------------------------------------------------------------------------------------

    /// <summary>Session still live, human, capability held. Returns the stopping outcome, or null to go on.</summary>
    private async Task<OpsActionOutcome?> GateAsync(OpsSession session, OpsActionKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!await signIn.IsActiveAsync(session, ct).ConfigureAwait(false))
        {
            return new OpsActionOutcome(OpsActionStatus.SessionEnded, "Your session ended. Sign in again.");
        }

        var identity = session.Identity;
        var allowed = false;
        if (identity.Kind == PrincipalKind.Human && permissions is not null)
        {
            foreach (var capability in CapabilitiesFor(kind))
            {
                try
                {
                    if (await permissions.IsAllowedAsync(identity.PrincipalId, identity.Kind, capability, Scope.Instance, ct).ConfigureAwait(false))
                    {
                        allowed = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail closed: a check that cannot answer is a no.
                    break;
                }
            }
        }

        if (allowed)
        {
            return null;
        }

        try
        {
            await audit.RecordAsync(
                AdminAuditKinds.Denied,
                identity.PrincipalId.ToString("D"),
                new Dictionary<string, string> { ["surface"] = "tui", ["action"] = kind.ToString() },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The refusal stands even when the denial could not be written.
        }

        return new OpsActionOutcome(OpsActionStatus.Forbidden, "Your account may not do that.");
    }

    private bool Confirm(string question) => widgets.YesNo(question, defaultYes: false) == true;

    /// <summary>Appends the audit entry before the write. False when it could not be appended, and the write must not run.</summary>
    private async Task<bool> TryAuditAsync(OpsSession session, string kind, params (string Key, string Value)[] fields)
    {
        var map = new Dictionary<string, string> { ["surface"] = "tui" };
        foreach (var (key, value) in fields)
        {
            map[key] = value;
        }

        try
        {
            await audit.RecordAsync(kind, session.Identity.PrincipalId.ToString("D"), map, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<OpsActionOutcome> WriteAsync(Func<Task<OpsWriteResult>> call, string doneMessage, string verb, string failedMessage)
    {
        OpsWriteResult result;
        try
        {
            result = await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(failedMessage);
        }

        return result.Done
            ? Done(doneMessage)
            : new OpsActionOutcome(OpsActionStatus.Refused, $"{verb} was refused" + (string.IsNullOrWhiteSpace(result.Reason) ? "." : ": " + TextFit.Cut(result.Reason, _messageWidth)));
    }

    private static string Problems(IReadOnlyList<AdminSelectionProblem> problems) =>
        problems.Count == 0
            ? "unknown problem."
            : TextFit.Cut(
                string.Join("; ", problems.Take(3).Select(p => $"{TextFit.Clean(p.ModuleId)}: {TextFit.Clean(p.Message)}"))
                + (problems.Count > 3 ? $" (+{problems.Count - 3} more)" : string.Empty),
                _messageWidth);

    /// <summary>A software-only key cannot approve (doc 32 section 5a), and no step-up channel exists, so the TUI tells the operator where to approve.</summary>
    private static OpsActionOutcome ApproveFromOwnerDevice() =>
        new(OpsActionStatus.Refused, "A terminal cannot approve. Approve it from the owner device (phone or Windows client).");

    private static OpsActionOutcome Done(string message) => new(OpsActionStatus.Done, message);

    private static OpsActionOutcome Cancelled() => new(OpsActionStatus.Cancelled, "Cancelled. Nothing was changed.");

    private static OpsActionOutcome Failed(string message) => new(OpsActionStatus.Failed, message);

    private static OpsActionOutcome Unavailable(string what) => new(OpsActionStatus.Unavailable, what + " is not available right now. Nothing was changed.");

    private static OpsActionOutcome AuditRefused() =>
        new(OpsActionStatus.Refused, "Refused: the audit log could not record it. Nothing was changed.");
}
