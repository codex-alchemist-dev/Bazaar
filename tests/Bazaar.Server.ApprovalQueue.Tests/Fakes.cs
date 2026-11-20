using System.Collections.Concurrent;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.ApprovalQueue.Tests;

/// <summary>In-memory <see cref="IApprovalStore"/> with the same guarded transitions as the SQL, for the state-machine tests.</summary>
internal sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<Guid, ApprovalItem> _items = new();

    public Task InsertAsync(ApprovalItem item, CancellationToken ct = default)
    {
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);

    public Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalItem>>([.. _items.Values
            .Where(i => i.Status == ApprovalStatus.Pending && i.ExpiresAt > now && (kind is null || i.Kind == kind))
            .OrderBy(i => i.RequestedAt)]);

    public Task<bool> TryDecideAsync(Guid id, Guid approverId, bool approve, string? reason, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(Update(id, i => i.Status == ApprovalStatus.Pending && i.ExpiresAt > now,
            i => i with { Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Denied, DecidedBy = approverId, DecidedAt = now, Reason = reason }));

    public Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(Update(id, i => i.Status == ApprovalStatus.Approved, i => i with { Status = ApprovalStatus.Consumed }));

    // Compare-and-swap loop, so concurrent callers cannot both win.
    private bool Update(Guid id, Func<ApprovalItem, bool> guard, Func<ApprovalItem, ApprovalItem> change)
    {
        while (_items.TryGetValue(id, out var current))
        {
            if (!guard(current))
                return false;
            if (_items.TryUpdate(id, change(current), current))
                return true;
        }

        return false;
    }
}

/// <summary>Role store and permission checker in one fake. A principal is allowed the approval capability when it was assigned with a grant.</summary>
internal sealed class FakeRoles : IRoleStore, IPermissionChecker
{
    private readonly Dictionary<Guid, (PrincipalKind Kind, bool Grant)> _principals = [];

    /// <summary>When set, the checker lets agents through too, to prove the queue's own tier 2 rule does not rely on it.</summary>
    public bool AllowAgents { get; set; }

    public void Assign(Guid principalId, PrincipalKind kind, bool grant) => _principals[principalId] = (kind, grant);

    public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
        Task.FromResult(capability == Capabilities.Approve
            && _principals.TryGetValue(principalId, out var p)
            && p.Grant
            && (kind != PrincipalKind.Agent || AllowAgents));

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForPrincipalAsync(Guid principalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RoleAssignment>>(_principals.TryGetValue(principalId, out var p)
            ? [new RoleAssignment(principalId, p.Kind, Guid.Empty, Guid.Empty, DateTimeOffset.UnixEpoch)]
            : []);

    public Task<Role?> GetAsync(Guid roleId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<Role?> GetByTemplateAsync(string templateKey, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> CreateAsync(Role role, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> ReplaceGrantsAsync(Guid roleId, IReadOnlyList<Grant> grants, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<UnassignResult> UnassignAsync(Guid principalId, Guid roleId, bool keepOneHuman, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForRoleAsync(Guid roleId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> AssignIfNoHumanHoldsAsync(RoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class RecordingAudit : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public IReadOnlyList<AuditEntry> Entries => [.. _entries];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        _entries.Enqueue(e);
        return Task.CompletedTask;
    }
}
