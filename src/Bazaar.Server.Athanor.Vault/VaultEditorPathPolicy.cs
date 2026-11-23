using Bazaar.Server.Memory.Access;
using Bazaar.Server.Vault.Markdown;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Athanor.Vault;

/// <summary>
/// Doc 18 "Access" applied to editor reads and saves of the Vault workspace. The owner reads and edits every file. A member human
/// reaches only a file with no <c>owner</c> in its front matter that sits under <c>company/</c> or <c>shared/</c>, or in a project area
/// (<c>projects/&lt;slug&gt;/</c>, <c>twin/&lt;slug&gt;/</c>) of a project they belong to; everything else (agent memory, <c>hive/</c>,
/// <c>decisions/</c>, drafts, <c>archive/</c>, root files) is the owner's only. A file whose front matter cannot be read is refused to a
/// member (fail closed). Other workspaces pass to <paramref name="next"/>.
/// </summary>
public sealed class VaultEditorPathPolicy(
    VaultEditorOptions options, IEditorWorkspaceStore store, IProjectMembership? membership, IEditorPathPolicy next) : IEditorPathPolicy
{
    // Front matter sits at the top of a file; reading this much is enough to find it without loading a large document twice.
    private const long _frontMatterReadCap = 1024 * 1024;

    public async ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, string path, EditorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!string.Equals(workspace.Id, options.WorkspaceId, StringComparison.Ordinal))
        {
            return await next.IsAllowedAsync(caller, workspace, path, access, cancellationToken).ConfigureAwait(false);
        }

        if (caller.IsOwner)
        {
            return true;
        }

        if (!InReadableArea(caller, path))
        {
            return false;
        }

        var read = await store.ReadAsync(workspace.Id, path, _frontMatterReadCap, cancellationToken).ConfigureAwait(false);
        switch (read.Status)
        {
            case EditorReadStatus.NotFound:
                // Nothing to protect: the handler answers not found, within an area the caller may see.
                return true;
            case EditorReadStatus.Ok:
                return !HasOwner(read.Content!);
            default:
                return false;
        }
    }

    private bool InReadableArea(EditorCaller caller, string path)
    {
        foreach (var prefix in VaultAccess.AgentReadablePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (VaultAccess.ProjectAreaOf(path) is not { } project || membership is null || !VaultPath.IsValidIdentifier(caller.UserId))
        {
            return false;
        }

        return membership.IsMember(VaultPrincipal.MemberHuman(caller.UserId), project);
    }

    private static bool HasOwner(byte[] content)
    {
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(content);
        }
        catch (ArgumentException)
        {
            return true;
        }

        var split = FrontMatterSplitter.Split(text);
        if (split.Yaml is null)
        {
            return false;
        }

        try
        {
            return VaultFrontMatterReader.Read(split.Yaml).Owner is not null;
        }
        catch (VaultFormatException)
        {
            return true;
        }
    }
}
