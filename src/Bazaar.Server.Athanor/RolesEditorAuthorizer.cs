using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.Athanor;

/// <summary>
/// <see cref="IEditorAuthorizer"/> over the role checker (doc 48 section 4, doc 15 capability classes). The caller is always a human
/// (agents never call the editor messages), and the caller's user id is the principal id. A non-GUID user id is refused.
/// Read asks for <c>read</c>; Write asks for <c>vault.write</c> (Vault) or <c>git.worktree.write</c> (git worktree). The scope is the
/// company for the Vault and the repository (the workspace id) for a worktree, so a grant at the company or instance covers both.
/// </summary>
public sealed class RolesEditorAuthorizer : IEditorAuthorizer
{
    /// <summary>Capability name for writing Vault files.</summary>
    public const string VaultWrite = "vault.write";

    /// <summary>Capability name for writing files in a git worktree.</summary>
    public const string GitWorktreeWrite = "git.worktree.write";

    private static readonly Capability _vaultWrite = new(VaultWrite);
    private static readonly Capability _gitWorktreeWrite = new(GitWorktreeWrite);

    private readonly IPermissionChecker _checker;

    public RolesEditorAuthorizer(IPermissionChecker checker)
    {
        ArgumentNullException.ThrowIfNull(checker);
        _checker = checker;
    }

    public async ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Guid.TryParse(caller.UserId, out var principal) || principal == Guid.Empty)
        {
            return false;
        }

        var capability = access == EditorAccess.Read
            ? Capabilities.Read
            : workspace.Kind == EditorWorkspaceKind.Vault ? _vaultWrite : _gitWorktreeWrite;
        var scope = workspace.Kind == EditorWorkspaceKind.Vault ? Scope.Company : new Scope(ScopeKind.Repository, workspace.Id);
        return await _checker.IsAllowedAsync(principal, PrincipalKind.Human, capability, scope, cancellationToken).ConfigureAwait(false);
    }
}
