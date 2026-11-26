namespace Bazaar.Server.Athanor;

/// <summary>The default path policy: the workspace scope alone decides. A workspace kind with its own rules replaces it (the Vault does).</summary>
public sealed class AllowAllEditorPathPolicy : IEditorPathPolicy
{
    /// <summary>The shared instance.</summary>
    public static AllowAllEditorPathPolicy Instance { get; } = new();

    public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, string path, EditorAccess access, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
