using Bazaar.Install.Abstractions;

namespace Bazaar.Server.Tests.Modules.Install;

/// <summary>An <see cref="IModuleInstallStore"/> that keeps entries in memory, so a test can see what was recorded and what was not.</summary>
internal sealed class InstallAuditLog : IModuleInstallStore
{
    public List<InstallAuditEntry> Entries { get; } = [];

    public List<ModulePermissionApprovalRecord> Approvals { get; } = [];

    public Task RecordInstalledAsync(InstallAuditEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordPermissionApprovalAsync(ModulePermissionApprovalRecord record, InstallAuditEntry entry, CancellationToken ct = default)
    {
        Approvals.Add(record);
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModulePermissionApprovalRecord>>([.. Approvals.Where(a => a.ModuleId == moduleId)]);
}
