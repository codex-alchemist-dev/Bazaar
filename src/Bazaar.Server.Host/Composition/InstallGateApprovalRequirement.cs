using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.Server.CompanyBuild.State;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Answers <see cref="IModuleApprovalRequirement"/> from the install gate's own permission rule (doc 48 section 1): a module needs the
/// owner while its declared permissions are not all covered by the owner-signed approvals for that version. The comparison is
/// <see cref="PermissionApproval.Evaluate"/>, not a copy of it. A module that declares nothing, or is unknown to the declaration
/// source, has nothing to approve. When permissions are declared but approvals cannot be read, the answer is yes (fail closed).
/// </summary>
public sealed class InstallGateApprovalRequirement(IModuleDeclarationSource declarations, IModulePermissionApprovalReader? approvals) : IModuleApprovalRequirement
{
    public async Task<bool> NeedsOwnerApprovalAsync(string moduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        var declaration = await declarations.FindAsync(moduleId, ct).ConfigureAwait(false);
        if (declaration is null || declaration.DeclaredPermissions.Count == 0)
        {
            return false;
        }

        if (approvals is null)
        {
            return true;
        }

        var records = await approvals.ListPermissionApprovalsAsync(declaration.ModuleId, declaration.Version, ct).ConfigureAwait(false);
        var approved = records.SelectMany(r => r.Permissions).ToHashSet(StringComparer.Ordinal);
        return !PermissionApproval.Evaluate(declaration.DeclaredPermissions, approved).IsApproved;
    }
}

/// <summary>
/// The host's declaration source today: no built-in module declares permissions (they ship in the signed host build), so none needs
/// approval. A module manager that reads installed package manifests replaces it by registering its own source first.
/// </summary>
public sealed class HostModuleDeclarationSource : IModuleDeclarationSource
{
    public Task<ModuleDeclaration?> FindAsync(string moduleId, CancellationToken ct = default) => Task.FromResult<ModuleDeclaration?>(null);
}
