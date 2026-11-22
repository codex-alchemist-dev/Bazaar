using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Bazaar.Core;
using Bazaar.Server.ApprovalQueue;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.CompanyBuild.State;

/// <summary>
/// The panel's module-selection and rebuild port (doc 51 section 5) over the stored selection and build history. It reuses
/// <see cref="CompanyBuildComposer"/> for validation and the plan, and the <see cref="ICompanyBuildExecutor"/> seam for the
/// build itself; it reimplements neither. The panel router has already passed the Modules area gate (<c>admin.modules</c>)
/// before any call reaches here, so the service takes the actor for the audit trail and does not re-check the permission.
/// </summary>
public sealed class ModuleSelectionService(
    ICompanyBuildStateStore store,
    IModuleCatalogue catalogue,
    IAuditLog audit,
    TimeProvider time,
    CompanyBuildStateOptions options,
    ICompanyBuildExecutor? executor = null,
    IApprovalQueue? approvals = null,
    IModuleApprovalRequirement? requirement = null) : IAdminModuleSelection
{
    /// <summary>The approval-queue kind filed for a module that needs the owner before it is built in.</summary>
    public const string ApprovalKind = "module.install";

    /// <summary>How long a filed approval stays open.</summary>
    public static readonly TimeSpan ApprovalTtl = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<Guid, Task> _runs = new();
    private readonly Lock _recoverLock = new();
    private Task? _recovery;

    public async Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default)
    {
        var state = await store.GetSelectionAsync(ct).ConfigureAwait(false);
        var selected = state.SelectedModuleIds.ToHashSet(StringComparer.Ordinal);
        var modules = catalogue.Manifests
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new AdminModuleInfo(m.Id, m.Id, m.Version, m.Kind.ToString(), [.. m.DependsOn], selected.Contains(m.Id)))
            .ToList();
        return new AdminModuleCatalogue(state.Revision, modules);
    }

    public async Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default)
    {
        var rows = await store.ListBuildsAsync(options.HistoryLimit, ct).ConfigureAwait(false);
        return [.. rows.Select(r => new AdminBuildRecord(r.Id.ToString("D"), r.Status, r.StartedAt, r.FinishedAt, r.ResultSummary, r.Attempts))];
    }

    public async Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModuleIds);
        var state = await store.GetSelectionAsync(ct).ConfigureAwait(false);
        var wanted = Normalise(selectedModuleIds);
        var problems = Validate(wanted, out var plan);
        if (plan is null)
            return new AdminSelectionPreview(false, problems, [], []);

        var after = plan.Modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var before = TryPlan(state.SelectedModuleIds)?.Modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        return new AdminSelectionPreview(
            true,
            problems,
            [.. after.Except(before).Order(StringComparer.Ordinal)],
            [.. before.Except(after).Order(StringComparer.Ordinal)]);
    }

    public async Task<AdminSelectionCommit> CommitAsync(
        Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModuleIds);
        var state = await store.GetSelectionAsync(ct).ConfigureAwait(false);
        if (state.Revision != expectedRevision)
            return new AdminSelectionCommit(false, state.Revision, []);

        var wanted = Normalise(selectedModuleIds);
        var problems = Validate(wanted, out var plan);
        if (plan is null)
            return new AdminSelectionCommit(false, state.Revision, problems);

        // Modules that still need the owner get a pending approval (reusing a live one); nothing is installed here.
        var notes = new List<AdminSelectionProblem>(problems);
        var pending = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var module in plan.Modules)
        {
            if (requirement is null || !await requirement.NeedsOwnerApprovalAsync(module.Id, ct).ConfigureAwait(false))
                continue;
            if (approvals is null)
            {
                // Fail closed: without the queue there is no way to ask the owner, so the module cannot be selected.
                return new AdminSelectionCommit(false, state.Revision, [.. problems, new AdminSelectionProblem(module.Id, "Needs owner approval and the approval queue is unavailable.")]);
            }

            var id = await ReuseOrRequestAsync(state, module.Id, actorPrincipalId, ct).ConfigureAwait(false);
            pending[module.Id] = id;
            notes.Add(new AdminSelectionProblem(module.Id, $"Awaiting owner approval (request {id:D}). It is not built in until approved."));
        }

        var now = time.GetUtcNow();
        var committed = await store.TryCommitSelectionAsync(expectedRevision, wanted, pending, actorPrincipalId, now, ct).ConfigureAwait(false);
        if (committed is null)
        {
            var current = await store.GetSelectionAsync(ct).ConfigureAwait(false);
            return new AdminSelectionCommit(false, current.Revision, []);
        }

        await AppendAuditAsync(now, actorPrincipalId, "module.selection.committed", new JsonObject
        {
            ["revision"] = committed.Revision,
            ["selected"] = Strings(wanted),
            ["pendingApprovals"] = Strings(pending.Keys.Order(StringComparer.Ordinal)),
        }, ct).ConfigureAwait(false);
        return new AdminSelectionCommit(true, committed.Revision, notes);
    }

    public async Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // A repeated key returns the first result and never starts anything.
        if (await store.GetBuildByKeyAsync(idempotencyKey, ct).ConfigureAwait(false) is { } first)
            return new AdminRebuildStart(true, first.Id.ToString("D"), null);

        await RecoverAbandonedOnceAsync(ct).ConfigureAwait(false);

        if (executor is null)
            return Refused("No company build runner is configured on this server.");

        var state = await store.GetSelectionAsync(ct).ConfigureAwait(false);
        var plan = TryPlan(state.SelectedModuleIds);
        if (plan is null)
            return Refused("The committed selection no longer composes. Commit a valid selection first.");

        var gate = await CheckApprovalsAsync(state, plan, ct).ConfigureAwait(false);
        if (gate.Refusal is not null)
            return Refused(gate.Refusal);

        var now = time.GetUtcNow();
        var buildId = Guid.NewGuid();
        var begun = await store.TryBeginBuildAsync(
            new BuildRow(buildId, state.Revision, "running", actorPrincipalId, now, null, idempotencyKey, 0, null), ct).ConfigureAwait(false);
        switch (begun.Outcome)
        {
            case BuildBeginOutcome.DuplicateKey when begun.Existing is { } raced:
                return new AdminRebuildStart(true, raced.Id.ToString("D"), null);
            case not BuildBeginOutcome.Started:
                return Refused("A rebuild is already running.");
        }

        try
        {
            // Spend the owner's approvals only once this build owns the single running slot.
            foreach (var approvalId in gate.ToConsume)
            {
                if (approvals is null || !await approvals.TryConsumeAsync(approvalId, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("An owner approval was already spent.");
            }

            if (gate.ToConsume.Count > 0)
                await store.TrySetPendingApprovalsAsync(state.Revision, new Dictionary<string, Guid>(), ct).ConfigureAwait(false);

            await AppendAuditAsync(now, actorPrincipalId, "module.rebuild.started", new JsonObject
            {
                ["buildId"] = buildId.ToString("D"),
                ["selectionRevision"] = state.Revision,
                ["idempotencyKey"] = idempotencyKey,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail closed: a build whose approval or audit step failed never runs.
            await store.CompleteBuildAsync(buildId, false, time.GetUtcNow(), 0, "Not started: an approval or audit step failed.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _runs[buildId] = Task.Run(() => RunBuildAsync(buildId, plan), CancellationToken.None);
        return new AdminRebuildStart(true, buildId.ToString("D"), null);
    }

    /// <summary>Completes when every build this process started has finished. For shutdown and tests.</summary>
    public Task DrainAsync() => Task.WhenAll(_runs.Values);

    private async Task RunBuildAsync(Guid buildId, CompanyBuildPlan plan)
    {
        try
        {
            var report = await executor!.RunAsync(plan, CancellationToken.None).ConfigureAwait(false);
            var attempts = report.Targets.Count == 0 ? 0 : report.Targets.Max(t => t.Report.Attempts);
            var failed = report.Targets.Count(t => !t.Report.Succeeded);
            var retries = report.Targets.Sum(t => t.Report.Retries);
            var summary = report.Complete
                ? string.Create(CultureInfo.InvariantCulture, $"Built {report.Targets.Count} target(s); {retries} retries.")
                : string.Create(CultureInfo.InvariantCulture, $"INCOMPLETE: {failed} of {report.Targets.Count} target(s) failed. The previous build stays live.");
            await store.CompleteBuildAsync(buildId, report.Complete, time.GetUtcNow(), attempts, summary, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Only the exception type: messages can carry local paths. The previous selection's build stays live.
            await store.CompleteBuildAsync(buildId, false, time.GetUtcNow(), 0, $"Failed: {e.GetType().Name}. The previous build stays live.", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runs.TryRemove(buildId, out _);
        }
    }

    private async Task<(string? Refusal, IReadOnlyList<Guid> ToConsume)> CheckApprovalsAsync(SelectionState state, CompanyBuildPlan plan, CancellationToken ct)
    {
        if (requirement is null)
            return (null, []);

        var toConsume = new List<Guid>();
        foreach (var module in plan.Modules)
        {
            if (!await requirement.NeedsOwnerApprovalAsync(module.Id, ct).ConfigureAwait(false))
                continue;
            if (approvals is null || !state.PendingApprovals.TryGetValue(module.Id, out var id))
                return ($"Module '{module.Id}' needs owner approval. Commit the selection to file the request.", []);

            var item = await approvals.GetAsync(id, ct).ConfigureAwait(false);
            switch (item?.Status)
            {
                case ApprovalStatus.Approved:
                    toConsume.Add(id);
                    break;
                case ApprovalStatus.Pending:
                    return ($"Module '{module.Id}' is awaiting owner approval (request {id:D}).", []);
                default:
                    return ($"The owner approval for module '{module.Id}' was denied, expired or already spent. Commit the selection again to file a new request.", []);
            }
        }

        return (null, toConsume);
    }

    private async Task<Guid> ReuseOrRequestAsync(SelectionState state, string moduleId, Guid actor, CancellationToken ct)
    {
        if (state.PendingApprovals.TryGetValue(moduleId, out var existing)
            && await approvals!.GetAsync(existing, ct).ConfigureAwait(false) is { Status: ApprovalStatus.Pending or ApprovalStatus.Approved })
        {
            return existing;
        }

        return await approvals!.RequestAsync(
            new NewApproval(ApprovalKind, 2, $"Build in module {moduleId}", actor, $"module:{moduleId}", ApprovalTtl), ct).ConfigureAwait(false);
    }

    private async Task RecoverAbandonedOnceAsync(CancellationToken ct)
    {
        Task recovery;
        lock (_recoverLock)
        {
            // This process has started no build yet, so a row still marked running is left over from a stopped server.
            _recovery ??= store.FailRunningBuildsAsync(time.GetUtcNow(), "Abandoned: the server stopped while this build ran.", CancellationToken.None);
            recovery = _recovery;
        }

        await recovery.WaitAsync(ct).ConfigureAwait(false);
    }

    private List<AdminSelectionProblem> Validate(IReadOnlyList<string> ids, out CompanyBuildPlan? plan)
    {
        var known = catalogue.Manifests.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var problems = ids.Where(id => !known.Contains(id)).Select(id => new AdminSelectionProblem(id, "Unknown module.")).ToList();
        plan = null;
        if (problems.Count > 0)
            return problems;

        try
        {
            plan = CompanyBuildComposer.Compose(catalogue.Manifests, new CompanyBuildRequest(options.CompanyId, ids));
        }
        catch (Exception e) when (e is CompanyBuildException or ComponentGraphException)
        {
            problems.Add(new AdminSelectionProblem(string.Empty, e.Message));
        }

        return problems;
    }

    private CompanyBuildPlan? TryPlan(IReadOnlyList<string> ids)
    {
        Validate(ids, out var plan);
        return plan;
    }

    private static string[] Normalise(IReadOnlyList<string> ids) =>
        [.. ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static AdminRebuildStart Refused(string reason) => new(false, null, reason);

    private Task AppendAuditAsync(DateTimeOffset ts, Guid actor, string kind, JsonObject payload, CancellationToken ct) =>
        audit.AppendAsync(new AuditEntry(ts, actor.ToString("D"), kind, payload.ToJsonString()), ct);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}
