namespace Bazaar.Install.Abstractions;

/// <summary>
/// The read seam for the owner-signed permission approvals (doc 48 section 1). It is the read side of <see cref="IModuleInstallStore"/>,
/// kept separate so a caller that only needs to know whether a module is approved cannot write an approval.
/// </summary>
public interface IModulePermissionApprovalReader
{
    /// <summary>The approvals recorded for one module version, oldest first. Empty when the owner never approved that version.</summary>
    Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, string version, CancellationToken ct = default);
}

/// <summary>The version and permission set a module declares in its signed manifest (doc 48 section 1).</summary>
public sealed record ModuleDeclaration(string ModuleId, string Version, IReadOnlySet<string> DeclaredPermissions);

/// <summary>Where the host learns what a module declares. Null means the module declares nothing, so the owner has nothing to approve.</summary>
public interface IModuleDeclarationSource
{
    Task<ModuleDeclaration?> FindAsync(string moduleId, CancellationToken ct = default);
}
