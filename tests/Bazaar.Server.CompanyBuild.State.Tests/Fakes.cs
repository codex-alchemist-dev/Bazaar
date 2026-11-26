using Bazaar.Core;
using Bazaar.Server.ApprovalQueue;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.CompanyBuild.State.Tests;

/// <summary>In-memory <see cref="ICompanyBuildStateStore"/> with the same guarded transitions as the SQL.</summary>
internal sealed class InMemoryStateStore : ICompanyBuildStateStore
{
    private readonly Lock _gate = new();
    private SelectionState _selection = new(0, [], new Dictionary<string, Guid>(), null, null);
    private readonly List<BuildRow> _builds = [];

    public Task<SelectionState> GetSelectionAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_selection);
    }

    public Task<SelectionState?> TryCommitSelectionAsync(long expectedRevision, IReadOnlyList<string> selectedModuleIds, IReadOnlyDictionary<string, Guid> pendingApprovals, Guid actor, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_selection.Revision != expectedRevision)
                return Task.FromResult<SelectionState?>(null);
            _selection = new SelectionState(expectedRevision + 1, [.. selectedModuleIds], new Dictionary<string, Guid>(pendingApprovals), actor, now);
            return Task.FromResult<SelectionState?>(_selection);
        }
    }

    public Task<bool> TrySetPendingApprovalsAsync(long revision, IReadOnlyDictionary<string, Guid> pendingApprovals, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_selection.Revision != revision)
                return Task.FromResult(false);
            _selection = _selection with { PendingApprovals = new Dictionary<string, Guid>(pendingApprovals) };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<BuildRow>> ListBuildsAsync(int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<BuildRow>>([.. _builds.OrderByDescending(b => b.StartedAt).Take(limit)]);
    }

    public Task<BuildRow?> GetBuildByKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_builds.FirstOrDefault(b => b.IdempotencyKey == idempotencyKey));
    }

    public Task<BuildBeginResult> TryBeginBuildAsync(BuildRow running, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_builds.FirstOrDefault(b => b.IdempotencyKey == running.IdempotencyKey) is { } dup)
                return Task.FromResult(new BuildBeginResult(BuildBeginOutcome.DuplicateKey, dup));
            if (_builds.Any(b => b.Status == "running"))
                return Task.FromResult(new BuildBeginResult(BuildBeginOutcome.AlreadyRunning, null));
            _builds.Add(running);
            return Task.FromResult(new BuildBeginResult(BuildBeginOutcome.Started, null));
        }
    }

    public Task CompleteBuildAsync(Guid id, bool succeeded, DateTimeOffset finishedAt, int attempts, string summary, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var i = _builds.FindIndex(b => b.Id == id && b.Status == "running");
            if (i >= 0)
                _builds[i] = _builds[i] with { Status = succeeded ? "succeeded" : "failed", FinishedAt = finishedAt, Attempts = attempts, ResultSummary = summary };
            return Task.CompletedTask;
        }
    }

    public Task<int> FailRunningBuildsAsync(DateTimeOffset now, string summary, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var count = 0;
            for (var i = 0; i < _builds.Count; i++)
            {
                if (_builds[i].Status != "running")
                    continue;
                _builds[i] = _builds[i] with { Status = "failed", FinishedAt = now, ResultSummary = summary };
                count++;
            }

            return Task.FromResult(count);
        }
    }

    public void SeedRunning(string key) =>
        _builds.Add(new BuildRow(Guid.NewGuid(), 0, "running", Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, key, 0, null));
}

internal sealed class FakeCatalogue(params ComponentManifest[] manifests) : IModuleCatalogue
{
    public IReadOnlyList<ComponentManifest> Manifests { get; } = manifests;

    public static ComponentManifest M(string id, params string[] dependsOn) =>
        new(id, "1.0.0", ComponentKind.Feature, dependsOn, []);
}

internal sealed class RecordingAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public bool Fail { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (Fail)
            throw new InvalidOperationException("audit down");
        lock (Entries)
            Entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>A build runner the test finishes by hand, so a build can stay running.</summary>
internal sealed class GatedExecutor : ICompanyBuildExecutor
{
    private readonly TaskCompletionSource<CompanyBuildSetReport> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Runs { get; private set; }

    public List<CompanyBuildPlan> Plans { get; } = [];

    public Task<CompanyBuildSetReport> RunAsync(CompanyBuildPlan plan, CancellationToken ct = default)
    {
        Runs++;
        Plans.Add(plan);
        return _gate.Task;
    }

    public void Finish(bool complete = true) =>
        _gate.SetResult(new CompanyBuildSetReport(
            "default",
            DateTimeOffset.UnixEpoch,
            [new CompanyBuildTargetReport("windows", new CompanyBuildReport(complete, complete ? 1 : 3, complete ? 0 : 2, complete ? [] : ["x", "y", "z"], "id.cs"))],
            complete,
            false,
            "out"));

    public void Throw() => _gate.SetException(new IOException("/secret/path failed"));
}

internal sealed class FakeApprovalQueue : IApprovalQueue
{
    private readonly Dictionary<Guid, ApprovalItem> _items = [];

    public List<NewApproval> Requests { get; } = [];

    public Task<Guid> RequestAsync(NewApproval request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var id = Guid.NewGuid();
        _items[id] = new ApprovalItem(id, request.Kind, request.Tier, request.Subject, request.RequesterPrincipalId, request.PayloadRef, ApprovalStatus.Pending, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + request.Ttl, null, null, null);
        return Task.FromResult(id);
    }

    public Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var i) ? i : null);

    public Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalItem>>([.. _items.Values.Where(i => i.Status == ApprovalStatus.Pending)]);

    public Task<ApprovalItem> DecideAsync(Guid id, Guid approverPrincipalId, bool approve, string? reason, CancellationToken ct = default)
    {
        _items[id] = _items[id] with { Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Denied, DecidedBy = approverPrincipalId };
        return Task.FromResult(_items[id]);
    }

    public Task<bool> TryConsumeAsync(Guid id, CancellationToken ct = default)
    {
        if (_items.TryGetValue(id, out var i) && i.Status == ApprovalStatus.Approved)
        {
            _items[id] = i with { Status = ApprovalStatus.Consumed };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}

internal sealed class FakeRequirement(params string[] needing) : IModuleApprovalRequirement
{
    public Task<bool> NeedsOwnerApprovalAsync(string moduleId, CancellationToken ct = default) =>
        Task.FromResult(needing.Contains(moduleId));
}
