using Bazaar.Client.Tui.Tests;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard.Tests;

internal sealed class FakePermissions : IPermissionChecker
{
    public HashSet<string> Held { get; } = [];

    public List<(Capability Capability, Scope Scope)> Asked { get; } = [];

    public bool Throw { get; set; }

    public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
    {
        Asked.Add((capability, scope));
        return Throw ? Task.FromException<bool>(new InvalidOperationException("db down")) : Task.FromResult(Held.Contains(capability.Value));
    }
}

/// <summary>Answers the confirm prompts and pickers from a script. A prompt with nothing left reads as cancelled.</summary>
internal sealed class FakeWidgets : IPromptWidgets
{
    public Queue<bool?> Confirms { get; } = new();

    public Queue<string?> Picks { get; } = new();

    public Queue<IReadOnlyList<string>?> Many { get; } = new();

    public List<string> Questions { get; } = [];

    public IReadOnlyList<string> LastOptions { get; private set; } = [];

    public string? TextEntry(string prompt, string? defaultValue) => defaultValue;

    public string? SecretEntry(string prompt) => null;

    public string? PickOne(string prompt, IReadOnlyList<string> options, string? defaultOption)
    {
        LastOptions = options;
        var wanted = Picks.Count == 0 ? null : Picks.Dequeue();
        return wanted is null ? null : options.First(o => o.Contains(wanted, StringComparison.Ordinal));
    }

    public IReadOnlyList<string>? PickMany(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults)
    {
        LastOptions = options;
        var wanted = Many.Count == 0 ? null : Many.Dequeue();
        return wanted?.Select(w => options.First(o => o.Contains(w, StringComparison.Ordinal))).ToList();
    }

    public bool? YesNo(string prompt, bool defaultYes)
    {
        Questions.Add(prompt);
        Assert.False(defaultYes, "a write confirm must default to No");
        return Confirms.Count == 0 ? null : Confirms.Dequeue();
    }
}

internal sealed class FakeModules : IAdminModuleSelection
{
    public AdminModuleCatalogue Catalogue { get; set; } = new(
        7,
        [
            new AdminModuleInfo("core.chat", "Chat", "1.0", "stable", [], Selected: true),
            new AdminModuleInfo("core.kanban", "Kanban", "1.0", "stable", [], Selected: false),
        ]);

    public AdminSelectionPreview Preview { get; set; } = new(true, [], ["core.kanban"], []);

    public AdminSelectionCommit Commit { get; set; } = new(true, 8, []);

    public AdminRebuildStart Rebuild { get; set; } = new(true, "build-1", null);

    public bool ThrowOnRebuild { get; set; }

    public int CommitCalls { get; private set; }

    public List<string> RebuildKeys { get; } = [];

    public (IReadOnlyList<string> Selected, long Expected)? LastCommit { get; private set; }

    public Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default) => Task.FromResult(Catalogue);

    public Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminBuildRecord>>([]);

    public Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default) => Task.FromResult(Preview);

    public Task<AdminSelectionCommit> CommitAsync(Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default)
    {
        CommitCalls++;
        LastCommit = (selectedModuleIds, expectedRevision);
        return Task.FromResult(Commit);
    }

    public Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default)
    {
        RebuildKeys.Add(idempotencyKey);
        if (ThrowOnRebuild)
        {
            ThrowOnRebuild = false;
            return Task.FromException<AdminRebuildStart>(new InvalidOperationException("secret detail"));
        }

        return Task.FromResult(Rebuild);
    }
}

internal sealed class FakeRevokers : IOpsMemberRevoker, IOpsMemberRestorer, IOpsDeviceRevoker
{
    public List<Guid> Members { get; } = [];

    public List<Guid> Restored { get; } = [];

    public Task<OpsWriteResult> RestoreMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default)
    {
        Restored.Add(principalId);
        return Task.FromResult(Result);
    }

    public List<Guid> Devices { get; } = [];

    public OpsWriteResult Result { get; set; } = new(true);

    public Task<OpsWriteResult> RevokeMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default)
    {
        Members.Add(principalId);
        return Task.FromResult(Result);
    }

    public Task<OpsWriteResult> RevokeDeviceAsync(Guid actorPrincipalId, Guid deviceId, CancellationToken ct = default)
    {
        Devices.Add(deviceId);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeDecider : IOpsApprovalDecider
{
    public List<(string Id, bool Approve, string? Proof)> Calls { get; } = [];

    public Task<OpsWriteResult> DecideAsync(Guid actorPrincipalId, string approvalId, bool approve, string? stepUpProof, CancellationToken ct = default)
    {
        Calls.Add((approvalId, approve, stepUpProof));
        return Task.FromResult(new OpsWriteResult(true));
    }
}

internal sealed class FakeStepUp(OpsStepUpResult result) : IOpsStepUp
{
    public List<OpsStepUpRequest> Requests { get; } = [];

    public Task<OpsStepUpResult> RequestAsync(OpsStepUpRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(result);
    }
}

internal sealed class FailingAudit : IAdminAudit
{
    public static bool FailKind(string kind) => kind != AdminAuditKinds.SignIn;

    public Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default) =>
        FailKind(kind) ? Task.FromException(new InvalidOperationException("audit store down")) : Task.CompletedTask;
}

public class OpsActionsTests
{
    private const string _proof = "SIGNED-PROOF-9f3c";

    private sealed class Rig
    {
        public FakeSessions Sessions { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public FakePermissions Permissions { get; } = new();

        public FakeWidgets Widgets { get; } = new();

        public FakeModules Modules { get; } = new();

        public FakeRevokers Revokers { get; } = new();

        public FakeDecider Decider { get; } = new();

        public FakeStepUp? StepUp { get; set; } = new(new OpsStepUpResult(true, _proof));

        public OpsPorts Reads { get; set; } = new();

        public OpsSession Session { get; private set; } = null!;

        public OpsActions Actions { get; private set; } = null!;

        public IAdminAudit ActionAudit { get; set; } = null!;

        public static async Task<Rig> CreateAsync(Action<Rig>? tweak = null, IAdminAudit? actionAudit = null)
        {
            var rig = new Rig();
            tweak?.Invoke(rig);
            var clock = new FakeClock(Samples.Now);
            var signIn = new OpsSignIn(new FakeOwnerGate(Samples.Owner), null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, clock);
            rig.Session = (await signIn.SignInOwnerAsync(TestContext.Current.CancellationToken))!;
            rig.Audit.Entries.Clear();
            rig.ActionAudit = actionAudit ?? rig.Audit;
            rig.Actions = new OpsActions(
                rig.Reads,
                new OpsWritePorts(rig.Modules, rig.Revokers, rig.Revokers, rig.Decider, rig.StepUp, rig.Revokers),
                rig.Permissions,
                rig.ActionAudit,
                signIn,
                rig.Widgets);
            return rig;
        }

        public Rig Grant(params Capability[] capabilities)
        {
            foreach (var capability in capabilities)
            {
                Permissions.Held.Add(capability.Value);
            }

            return this;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Capability gate -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_action_asks_for_the_capability_the_web_panel_asks_for_that_area()
    {
        Assert.Equal([Capabilities.AdminModules], OpsActions.CapabilitiesFor(OpsActionKind.StartRebuild));
        Assert.Equal([Capabilities.AdminModules], OpsActions.CapabilitiesFor(OpsActionKind.CommitSelection));
        Assert.Equal([Capabilities.AdminMembers], OpsActions.CapabilitiesFor(OpsActionKind.RevokeMember));
        Assert.Equal([Capabilities.AdminDevices], OpsActions.CapabilitiesFor(OpsActionKind.RevokeDevice));
        Assert.Equal([Capabilities.Approve], OpsActions.CapabilitiesFor(OpsActionKind.Approve));
        Assert.Equal([Capabilities.Approve], OpsActions.CapabilitiesFor(OpsActionKind.Deny));
    }

    [Fact]
    public async Task Without_the_capability_nothing_is_asked_or_written_and_the_denial_is_audited()
    {
        var rig = await Rig.CreateAsync();
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Forbidden, outcome.Status);
        Assert.Empty(rig.Modules.RebuildKeys);
        Assert.Empty(rig.Widgets.Questions);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal(AdminAuditKinds.Denied, entry.Kind);
        Assert.Equal("StartRebuild", entry.Fields["action"]);
        Assert.Equal("tui", entry.Fields["surface"]);
        Assert.Equal(Scope.Instance, Assert.Single(rig.Permissions.Asked).Scope);
    }

    [Fact]
    public async Task A_capability_for_another_area_does_not_open_this_one()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminMembers);

        var outcome = await rig.Actions.RevokeDeviceAsync(rig.Session, Guid.NewGuid(), "phone", Ct);

        Assert.Equal(OpsActionStatus.Forbidden, outcome.Status);
        Assert.Empty(rig.Revokers.Devices);
    }

    [Fact]
    public async Task A_permission_check_that_throws_is_a_refusal()
    {
        var rig = await Rig.CreateAsync(r => { r.Permissions.Throw = true; r.Permissions.Held.Add("admin.modules"); });

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Forbidden, outcome.Status);
        Assert.Empty(rig.Modules.RebuildKeys);
    }

    [Fact]
    public async Task An_agent_is_refused_even_when_a_row_grants_the_capability()
    {
        var rig = await Rig.CreateAsync(r => r.Grant(Capabilities.AdminModules));
        var agent = new OpsSession(
            new AdminIdentity(Guid.NewGuid(), PrincipalKind.Agent, Guid.NewGuid()), "cookie-0", rig.Session.Panel, OpsSignInMethod.AdminCode);

        var outcome = await rig.Actions.StartRebuildAsync(agent, Ct);

        Assert.Equal(OpsActionStatus.Forbidden, outcome.Status);
        Assert.Empty(rig.Permissions.Asked);
    }

    [Fact]
    public async Task A_missing_permission_checker_fails_closed()
    {
        var rig = await Rig.CreateAsync();
        var actions = new OpsActions(rig.Reads, new OpsWritePorts(rig.Modules), permissions: null, rig.Audit, new OpsSignIn(null, null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, TimeProvider.System), rig.Widgets);

        Assert.Equal(OpsActionStatus.Forbidden, (await actions.StartRebuildAsync(rig.Session, Ct)).Status);
    }

    [Fact]
    public async Task A_revoked_session_cannot_act()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Sessions.RevokeAll();

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.SessionEnded, outcome.Status);
        Assert.Empty(rig.Modules.RebuildKeys);
    }

    [Fact]
    public async Task A_missing_port_reads_unavailable_and_changes_nothing()
    {
        var rig = await Rig.CreateAsync();
        var actions = new OpsActions(rig.Reads, new OpsWritePorts(), rig.Permissions, rig.Audit, new OpsSignIn(null, null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, TimeProvider.System), rig.Widgets);

        Assert.Equal(OpsActionStatus.Unavailable, (await actions.StartRebuildAsync(rig.Session, Ct)).Status);
        Assert.Equal(OpsActionStatus.Unavailable, (await actions.RevokeMemberAsync(rig.Session, Guid.NewGuid(), null, Ct)).Status);
        Assert.Equal(OpsActionStatus.Unavailable, (await actions.RevokeDeviceAsync(rig.Session, Guid.NewGuid(), null, Ct)).Status);
        Assert.Equal(OpsActionStatus.Refused, (await actions.DecideApprovalAsync(rig.Session, "a1", true, null, Ct)).Status);
        Assert.Empty(rig.Audit.Entries);
    }

    // ---- Rebuild -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_rebuild_confirms_audits_with_the_idempotency_key_then_starts()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Contains("build-1", outcome.Message);
        var key = Assert.Single(rig.Modules.RebuildKeys);
        Assert.StartsWith("tui-", key);
        Assert.InRange(key.Length, 1, 128);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal(AdminAuditKinds.RebuildStart, entry.Kind);
        Assert.Equal(key, entry.Fields["idempotencyKey"]);
        Assert.Equal(Samples.Owner.PrincipalId.ToString("D"), entry.Actor);
        Assert.Single(rig.Widgets.Questions);
    }

    [Fact]
    public async Task Saying_no_to_the_confirm_writes_nothing_and_audits_nothing()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(false);

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Cancelled, outcome.Status);
        Assert.Empty(rig.Modules.RebuildKeys);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task A_closed_prompt_counts_as_no()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(null);

        Assert.Equal(OpsActionStatus.Cancelled, (await rig.Actions.StartRebuildAsync(rig.Session, Ct)).Status);
        Assert.Empty(rig.Modules.RebuildKeys);
    }

    [Fact]
    public async Task A_rebuild_already_running_is_a_conflict_with_the_servers_reason()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.Rebuild = new AdminRebuildStart(false, null, "a rebuild is already running (build-0)"))).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Conflict, outcome.Status);
        Assert.Contains("already running", outcome.Message);
    }

    [Fact]
    public async Task A_rebuild_that_threw_is_retried_with_the_same_key_then_a_fresh_key_follows_an_answer()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.ThrowOnRebuild = true)).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);
        rig.Widgets.Confirms.Enqueue(true);
        rig.Widgets.Confirms.Enqueue(true);

        var failed = await rig.Actions.StartRebuildAsync(rig.Session, Ct);
        var retried = await rig.Actions.StartRebuildAsync(rig.Session, Ct);
        var next = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Failed, failed.Status);
        Assert.DoesNotContain("secret detail", failed.Message);
        Assert.Equal(OpsActionStatus.Done, retried.Status);
        Assert.Equal(rig.Modules.RebuildKeys[0], rig.Modules.RebuildKeys[1]);
        Assert.NotEqual(rig.Modules.RebuildKeys[1], rig.Modules.RebuildKeys[2]);
        Assert.Equal(OpsActionStatus.Done, next.Status);
    }

    [Fact]
    public async Task When_the_audit_entry_cannot_be_appended_the_rebuild_is_refused_and_never_called()
    {
        var rig = (await Rig.CreateAsync(r => r.Grant(Capabilities.AdminModules), new FailingAudit()));
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.StartRebuildAsync(rig.Session, Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Empty(rig.Modules.RebuildKeys);
    }

    // ---- Module selection ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Committing_a_selection_previews_confirms_audits_and_commits_against_the_revision_the_operator_saw()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.CommitSelectionAsync(rig.Session, ["core.chat", "core.kanban"], 7, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Contains("revision 8", outcome.Message);
        Assert.Equal(["core.chat", "core.kanban"], rig.Modules.LastCommit!.Value.Selected);
        Assert.Equal(7, rig.Modules.LastCommit.Value.Expected);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal(AdminAuditKinds.SelectionCommit, entry.Kind);
        Assert.Equal("7", entry.Fields["expectedRevision"]);
        Assert.Equal("core.chat,core.kanban", entry.Fields["selected"]);
    }

    [Fact]
    public async Task A_stale_revision_is_a_conflict_that_names_the_current_revision()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.Commit = new AdminSelectionCommit(false, 9, []))).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.CommitSelectionAsync(rig.Session, ["core.kanban"], 7, Ct);

        Assert.Equal(OpsActionStatus.Conflict, outcome.Status);
        Assert.Contains("revision 9", outcome.Message);
        Assert.Equal(1, rig.Modules.CommitCalls);
    }

    [Fact]
    public async Task An_invalid_selection_is_refused_at_the_dry_run_with_no_prompt_and_no_commit()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.Preview = new AdminSelectionPreview(false, [new AdminSelectionProblem("core.kanban", "needs core.chat")], [], []))).Grant(Capabilities.AdminModules);

        var outcome = await rig.Actions.CommitSelectionAsync(rig.Session, ["core.kanban"], 7, Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Contains("needs core.chat", outcome.Message);
        Assert.Equal(0, rig.Modules.CommitCalls);
        Assert.Empty(rig.Widgets.Questions);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task A_commit_the_server_refuses_with_problems_is_refused_not_a_conflict()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.Commit = new AdminSelectionCommit(false, 7, [new AdminSelectionProblem("core.kanban", "missing dependency")]))).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.CommitSelectionAsync(rig.Session, ["core.kanban"], 7, Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Contains("missing dependency", outcome.Message);
    }

    [Fact]
    public async Task An_unchanged_selection_is_not_committed()
    {
        var rig = (await Rig.CreateAsync(r => r.Modules.Preview = new AdminSelectionPreview(true, [], [], []))).Grant(Capabilities.AdminModules);

        var outcome = await rig.Actions.CommitSelectionAsync(rig.Session, ["core.chat"], 7, Ct);

        Assert.Equal(OpsActionStatus.Cancelled, outcome.Status);
        Assert.Equal(0, rig.Modules.CommitCalls);
    }

    [Fact]
    public async Task The_selection_picker_reads_the_catalogue_and_commits_with_its_revision()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Many.Enqueue(["core.chat", "core.kanban"]);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RunAsync(rig.Session, OpsActionKind.CommitSelection, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal(7, rig.Modules.LastCommit!.Value.Expected);
        Assert.Equal(["core.chat", "core.kanban"], rig.Modules.LastCommit.Value.Selected);
    }

    [Fact]
    public async Task Cancelling_the_selection_picker_changes_nothing()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Many.Enqueue(null);

        Assert.Equal(OpsActionStatus.Cancelled, (await rig.Actions.RunAsync(rig.Session, OpsActionKind.CommitSelection, Ct)).Status);
        Assert.Equal(0, rig.Modules.CommitCalls);
        Assert.Empty(rig.Audit.Entries);
    }

    // ---- Revocation ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_a_person_needs_the_members_capability_a_confirm_and_an_audit_entry()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminMembers);
        var target = Guid.NewGuid();
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RevokeMemberAsync(rig.Session, target, "Dana", Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal([target], rig.Revokers.Members);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal(OpsAuditKinds.MemberRevoke, entry.Kind);
        Assert.Equal(target.ToString("D"), entry.Fields["principal"]);
        Assert.Contains("Dana", Assert.Single(rig.Widgets.Questions));
    }

    [Fact]
    public async Task Unsuspending_a_person_needs_the_members_capability_a_confirm_and_an_audit_entry()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminMembers);
        var target = Guid.NewGuid();
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RestoreMemberAsync(rig.Session, target, "Dana", Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal([target], rig.Revokers.Restored);
        Assert.Equal(OpsAuditKinds.MemberRestore, Assert.Single(rig.Audit.Entries).Kind);
        Assert.Equal([Capabilities.AdminMembers], OpsActions.CapabilitiesFor(OpsActionKind.RestoreMember));
        Assert.Equal(OpsActionKind.RestoreMember, OpsActions.KeyFor("admin.users", 's'));
    }

    [Fact]
    public async Task Unsuspending_without_the_capability_is_forbidden_and_never_calls_the_port()
    {
        var rig = await Rig.CreateAsync();

        var outcome = await rig.Actions.RestoreMemberAsync(rig.Session, Guid.NewGuid(), "Dana", Ct);

        Assert.Equal(OpsActionStatus.Forbidden, outcome.Status);
        Assert.Empty(rig.Revokers.Restored);
    }

    [Fact]
    public async Task The_unsuspend_picker_lists_only_revoked_people()
    {
        var alive = new AdminUser(Guid.NewGuid(), "Alive", "coworker", false, false);
        var gone = new AdminUser(Guid.NewGuid(), "Suspended", "coworker", false, true);
        var rig = (await Rig.CreateAsync(r => r.Reads = new OpsPorts(Users: new FakeUsers(alive, gone)))).Grant(Capabilities.AdminMembers);
        rig.Widgets.Picks.Enqueue("Suspended");
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RunAsync(rig.Session, OpsActionKind.RestoreMember, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal([gone.PrincipalId], rig.Revokers.Restored);
    }

    [Fact]
    public async Task Approving_without_a_step_up_sends_the_operator_to_the_owner_device()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.Approve);
        var actions = new OpsActions(rig.Reads, new OpsWritePorts(), rig.Permissions, rig.Audit, new OpsSignIn(null, null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, TimeProvider.System), rig.Widgets);

        var outcome = await actions.DecideApprovalAsync(rig.Session, "appr-1", approve: true, "merge", Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Contains("owner device", outcome.Message);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task The_approvals_screen_lists_what_waits_and_names_the_owner_device_as_the_place_to_approve()
    {
        var waiting = new AdminApproval("appr-1", "merge pull request 12", "Charles", DateTimeOffset.UnixEpoch);
        var lines = await OpsScreens.ApprovalsAsync(new OpsPorts(Approvals: new FakeApprovals(waiting)), Samples.Context(), Ct);

        Assert.Contains(lines, l => l.Contains("merge pull request 12", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("owner device", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Declining_a_revocation_leaves_the_person_alone()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminMembers);
        rig.Widgets.Confirms.Enqueue(false);

        var outcome = await rig.Actions.RevokeMemberAsync(rig.Session, Guid.NewGuid(), "Dana", Ct);

        Assert.Equal(OpsActionStatus.Cancelled, outcome.Status);
        Assert.Empty(rig.Revokers.Members);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task A_refusal_from_the_server_port_is_shown_as_refused()
    {
        var rig = (await Rig.CreateAsync(r => r.Revokers.Result = new OpsWriteResult(false, "the last owner cannot be revoked"))).Grant(Capabilities.AdminMembers);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RevokeMemberAsync(rig.Session, Guid.NewGuid(), "Owner", Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Contains("last owner", outcome.Message);
    }

    [Fact]
    public async Task Revoking_a_device_needs_the_devices_capability_and_is_audited()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminDevices);
        var device = Guid.NewGuid();
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RevokeDeviceAsync(rig.Session, device, "Dana's phone", Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal([device], rig.Revokers.Devices);
        Assert.Equal(OpsAuditKinds.DeviceRevoke, Assert.Single(rig.Audit.Entries).Kind);
    }

    [Fact]
    public async Task When_the_audit_entry_cannot_be_appended_a_revocation_is_refused_and_never_called()
    {
        var rig = await Rig.CreateAsync(r => r.Grant(Capabilities.AdminMembers, Capabilities.AdminDevices), new FailingAudit());
        rig.Widgets.Confirms.Enqueue(true);
        rig.Widgets.Confirms.Enqueue(true);

        Assert.Equal(OpsActionStatus.Refused, (await rig.Actions.RevokeMemberAsync(rig.Session, Guid.NewGuid(), null, Ct)).Status);
        Assert.Equal(OpsActionStatus.Refused, (await rig.Actions.RevokeDeviceAsync(rig.Session, Guid.NewGuid(), null, Ct)).Status);
        Assert.Empty(rig.Revokers.Members);
        Assert.Empty(rig.Revokers.Devices);
    }

    [Fact]
    public async Task The_pickers_list_only_live_people_and_devices_and_clean_their_names()
    {
        var alive = new AdminUser(Guid.NewGuid(), "Dana\u001b[31m", "coworker", false, false);
        var gone = new AdminUser(Guid.NewGuid(), "Gone", "coworker", false, true);
        var rig = (await Rig.CreateAsync(r => r.Reads = new OpsPorts(Users: new FakeUsers(alive, gone)))).Grant(Capabilities.AdminMembers);
        rig.Widgets.Picks.Enqueue("Dana");
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RunAsync(rig.Session, OpsActionKind.RevokeMember, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal([alive.PrincipalId], rig.Revokers.Members);
        var option = Assert.Single(rig.Widgets.LastOptions);
        Assert.DoesNotContain('\u001b', option);
        Assert.DoesNotContain("Gone", option);
    }

    // ---- Approvals -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_asks_the_step_up_and_hands_its_proof_to_the_decider_but_never_to_the_audit()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.Approve);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.DecideApprovalAsync(rig.Session, "ap-1", approve: true, "push to main", Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        var call = Assert.Single(rig.Decider.Calls);
        Assert.Equal(("ap-1", true, _proof), call);
        var request = Assert.Single(rig.StepUp!.Requests);
        Assert.Equal("ap-1", request.Subject);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal(OpsAuditKinds.ApprovalDecide, entry.Kind);
        Assert.Equal("approve", entry.Fields["decision"]);
        Assert.DoesNotContain(_proof, string.Join("|", entry.Fields.Values) + entry.Actor);
        Assert.DoesNotContain(_proof, outcome.Message);
    }

    [Fact]
    public async Task Approving_without_a_step_up_wired_is_refused_and_the_decider_is_never_called()
    {
        var rig = (await Rig.CreateAsync(r => r.StepUp = null)).Grant(Capabilities.Approve);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.DecideApprovalAsync(rig.Session, "ap-1", approve: true, null, Ct);

        Assert.Equal(OpsActionStatus.Refused, outcome.Status);
        Assert.Contains("client", outcome.Message);
        Assert.Empty(rig.Decider.Calls);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task A_declined_step_up_or_one_without_proof_approves_nothing()
    {
        var declined = (await Rig.CreateAsync(r => r.StepUp = new FakeStepUp(new OpsStepUpResult(false, null, "no device answered")))).Grant(Capabilities.Approve);
        declined.Widgets.Confirms.Enqueue(true);
        var empty = (await Rig.CreateAsync(r => r.StepUp = new FakeStepUp(new OpsStepUpResult(true, null)))).Grant(Capabilities.Approve);
        empty.Widgets.Confirms.Enqueue(true);

        var first = await declined.Actions.DecideApprovalAsync(declined.Session, "ap-1", true, null, Ct);
        var second = await empty.Actions.DecideApprovalAsync(empty.Session, "ap-1", true, null, Ct);

        Assert.Equal(OpsActionStatus.Refused, first.Status);
        Assert.Contains("no device answered", first.Message);
        Assert.Equal(OpsActionStatus.Refused, second.Status);
        Assert.Empty(declined.Decider.Calls);
        Assert.Empty(empty.Decider.Calls);
    }

    [Fact]
    public async Task Refusing_a_request_needs_no_step_up_but_still_needs_the_capability_a_confirm_and_an_audit_entry()
    {
        var rig = (await Rig.CreateAsync(r => r.StepUp = null)).Grant(Capabilities.Approve);
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.DecideApprovalAsync(rig.Session, "ap-2", approve: false, "send mail", Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal(("ap-2", false, (string?)null), Assert.Single(rig.Decider.Calls));
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Equal("deny", entry.Fields["decision"]);
    }

    [Fact]
    public async Task Without_the_approval_capability_neither_approving_nor_refusing_goes_through()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules, Capabilities.AdminMembers);

        Assert.Equal(OpsActionStatus.Forbidden, (await rig.Actions.DecideApprovalAsync(rig.Session, "ap-1", true, null, Ct)).Status);
        Assert.Equal(OpsActionStatus.Forbidden, (await rig.Actions.DecideApprovalAsync(rig.Session, "ap-1", false, null, Ct)).Status);
        Assert.Empty(rig.Decider.Calls);
        Assert.Empty(rig.StepUp!.Requests);
    }

    [Fact]
    public async Task The_approval_picker_lists_pending_requests_and_decides_the_chosen_one()
    {
        var pending = new AdminApproval("ap-9", "merge PR 12", "Charles", Samples.Now);
        var rig = (await Rig.CreateAsync(r => r.Reads = new OpsPorts(Approvals: new FakeApprovals(pending)))).Grant(Capabilities.Approve);
        rig.Widgets.Picks.Enqueue("ap-9");
        rig.Widgets.Confirms.Enqueue(true);

        var outcome = await rig.Actions.RunAsync(rig.Session, OpsActionKind.Approve, Ct);

        Assert.Equal(OpsActionStatus.Done, outcome.Status);
        Assert.Equal("ap-9", Assert.Single(rig.Decider.Calls).Id);
        Assert.Contains("merge PR 12", Assert.Single(rig.Widgets.Questions));
    }

    // ---- Secrets and keys ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_audit_entry_or_message_from_any_action_carries_the_proof_or_a_cookie()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.Approve, Capabilities.AdminModules, Capabilities.AdminMembers, Capabilities.AdminDevices);
        for (var i = 0; i < 5; i++)
        {
            rig.Widgets.Confirms.Enqueue(true);
        }

        var outcomes = new List<OpsActionOutcome>
        {
            await rig.Actions.DecideApprovalAsync(rig.Session, "ap-1", true, null, Ct),
            await rig.Actions.StartRebuildAsync(rig.Session, Ct),
            await rig.Actions.CommitSelectionAsync(rig.Session, ["core.chat"], 7, Ct),
            await rig.Actions.RevokeMemberAsync(rig.Session, Guid.NewGuid(), "x", Ct),
            await rig.Actions.RevokeDeviceAsync(rig.Session, Guid.NewGuid(), "y", Ct),
        };

        var everything = string.Join("\n", rig.Audit.Entries.Select(e => e.Actor + e.Kind + string.Join("|", e.Fields.Select(f => f.Key + f.Value))))
            + string.Join("\n", outcomes.Select(o => o.Message));
        Assert.DoesNotContain(_proof, everything);
        Assert.DoesNotContain("cookie-", everything);
        Assert.All(rig.Audit.Entries, e => Assert.Equal("tui", e.Fields["surface"]));
    }

    [Theory]
    [InlineData("admin.modules", 'c', OpsActionKind.CommitSelection)]
    [InlineData("admin.modules", 'b', OpsActionKind.StartRebuild)]
    [InlineData("admin.rebuilds", 'B', OpsActionKind.StartRebuild)]
    [InlineData("admin.users", 'x', OpsActionKind.RevokeMember)]
    [InlineData("admin.users", 'v', OpsActionKind.RevokeDevice)]
    [InlineData("admin.approvals", 'y', OpsActionKind.Approve)]
    [InlineData("admin.approvals", 'n', OpsActionKind.Deny)]
    public void Action_keys_belong_to_their_screen(string screen, char key, OpsActionKind expected)
    {
        Assert.Equal(expected, OpsActions.KeyFor(screen, key));
        Assert.Null(OpsActions.KeyFor(DefaultMenus.Health, key));
    }

    [Fact]
    public void Action_keys_never_shadow_a_menu_shortcut()
    {
        var shortcuts = DefaultMenus.Create()
            .SelectMany(m => new[] { m.Shortcut }.Concat(m.Items.Select(i => i.Shortcut)))
            .OfType<char>()
            .Select(char.ToLowerInvariant)
            .ToHashSet();

        foreach (var screen in new[] { DefaultMenus.Modules, DefaultMenus.Rebuilds, DefaultMenus.Users, DefaultMenus.Approvals })
        {
            for (var key = 'a'; key <= 'z'; key++)
            {
                if (OpsActions.KeyFor(screen, key) is not null)
                {
                    Assert.DoesNotContain(key, shortcuts);
                }
            }
        }
    }

    // ---- In the shell --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shell_runs_an_action_key_on_its_screen_shows_the_outcome_and_clears_it_on_the_next_key()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        rig.Widgets.Confirms.Enqueue(true);
        var terminal = new ScriptedTerminal(supportsKeys: true);
        var clock = new FakeClock(Samples.Now);
        var signIn = new OpsSignIn(new FakeOwnerGate(Samples.Owner), null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, clock);
        var shell = new DashboardShell(terminal, rig.Reads, signIn, TuiStyle.Plain, clock, TimeZoneInfo.Utc, actions: rig.Actions);
        shell.HandleKey(TerminalKey.Of('a'));
        shell.HandleKey(TerminalKey.Of('r'));
        Assert.Equal(DefaultMenus.Rebuilds, shell.CurrentScreen);
        Assert.False(shell.Menu.IsOpen);

        var keep = await shell.HandleKeyAsync(TerminalKey.Of('b'), rig.Session, Ct);
        var frame = await shell.RenderAsync(Ct);

        Assert.True(keep);
        Assert.Equal(OpsActionStatus.Done, shell.LastAction!.Status);
        Assert.Contains(frame.Lines, l => l.Contains("Rebuild started", StringComparison.Ordinal));
        Assert.True(frame.Lines.Count <= 24);

        await shell.HandleKeyAsync(new TerminalKey(TerminalKeyKind.Tab), rig.Session, Ct);
        Assert.Null(shell.LastAction);
    }

    [Fact]
    public async Task A_shell_without_actions_ignores_action_keys_and_stays_read_only()
    {
        var rig = (await Rig.CreateAsync()).Grant(Capabilities.AdminModules);
        var terminal = new ScriptedTerminal(supportsKeys: true);
        var clock = new FakeClock(Samples.Now);
        var signIn = new OpsSignIn(new FakeOwnerGate(Samples.Owner), null, rig.Sessions, new FakeAuthorizer(true), rig.Audit, clock);
        var shell = new DashboardShell(terminal, rig.Reads, signIn, TuiStyle.Plain, clock, TimeZoneInfo.Utc);
        shell.HandleKey(TerminalKey.Of('a'));
        shell.HandleKey(TerminalKey.Of('r'));

        await shell.HandleKeyAsync(TerminalKey.Of('b'), rig.Session, Ct);

        Assert.Null(shell.LastAction);
        Assert.Empty(rig.Modules.RebuildKeys);
    }
}

internal sealed class FakeUsers(params AdminUser[] users) : IAdminUserDirectory
{
    public Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AdminUser>>(users);
}

internal sealed class FakeApprovals(params AdminApproval[] approvals) : IAdminApprovalSource
{
    public Task<IReadOnlyList<AdminApproval>> ListPendingAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AdminApproval>>(approvals);
}
