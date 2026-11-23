namespace Bazaar.Server.Athanor;

/// <summary>What a workspace is backed by; decides the write scope (doc 57 section 6).</summary>
public enum EditorWorkspaceKind
{
    /// <summary>The Vault repo; needs <c>vault.write</c>.</summary>
    Vault,

    /// <summary>An agent worktree or project repo; needs <c>git.worktree.write</c>.</summary>
    GitWorktree,
}

/// <summary>A workspace the server controls.</summary>
public sealed record EditorWorkspaceInfo(string Id, EditorWorkspaceKind Kind, bool ReadOnly);

/// <summary>Kind of access an authorizer is asked about.</summary>
public enum EditorAccess
{
    Read,
    Write,
}

/// <summary>Decides whether a caller holds the scope for a workspace (<c>vault.write</c> or <c>git.worktree.write</c>; doc 48, doc 15).</summary>
public interface IEditorAuthorizer
{
    ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="IEditorWorkspaceStore.ReadAsync"/>.</summary>
public enum EditorReadStatus
{
    Ok,
    NotFound,
    TooLarge,

    /// <summary>The path is refused by the store (a link, a <c>.git</c> segment, a device name, an escape). Maps to <c>editor.forbidden</c>.</summary>
    Blocked,
}

/// <summary>A read result. <see cref="Content"/> is null unless <see cref="Status"/> is Ok.</summary>
public sealed record EditorReadResult(EditorReadStatus Status, long SizeBytes = 0, string? Revision = null, byte[]? Content = null);

/// <summary>Result of <see cref="IEditorWorkspaceStore.WriteAsync"/>.</summary>
public enum EditorWriteStatus
{
    Written,
    Conflict,
    NotFound,

    /// <summary>The path is refused, or the workspace is read-only. Maps to <c>editor.forbidden</c>.</summary>
    Blocked,

    /// <summary>The file on the server is over the open limit, so it cannot be returned as a conflict copy. Maps to <c>editor.too_large</c>.</summary>
    TooLarge,
}

/// <summary>A write result: <see cref="NewRevision"/> when Written; the current server copy when Conflict.</summary>
public sealed record EditorWriteResult(EditorWriteStatus Status, string? NewRevision = null, string? ServerRevision = null, byte[]? ServerContent = null);

/// <summary>
/// Storage seam. The real implementation resolves workspace ids through the Vault (doc 18) and git (doc 21) providers, resolves
/// symlinks and rejects any that leave the workspace root. Paths arrive already normalised (relative, '/'-separated, no '..').
/// Implementations throw <see cref="EditorStoreUnavailableException"/> when the backend cannot answer.
/// </summary>
public interface IEditorWorkspaceStore
{
    /// <summary>The workspace, or null when the id is unknown.</summary>
    ValueTask<EditorWorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken);

    /// <summary>Reads a file, refusing without loading it when it is larger than <paramref name="maxBytes"/>.</summary>
    ValueTask<EditorReadResult> ReadAsync(string workspaceId, string path, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Compare-and-set write: writes only when the file's current revision equals <paramref name="baseRevision"/>, atomically.</summary>
    ValueTask<EditorWriteResult> WriteAsync(string workspaceId, string path, string baseRevision, byte[] content, CancellationToken cancellationToken);
}

/// <summary>The backend cannot answer; maps to <c>editor.unavailable</c>.</summary>
public sealed class EditorStoreUnavailableException : Exception
{
    public EditorStoreUnavailableException()
    {
    }

    public EditorStoreUnavailableException(string message)
        : base(message)
    {
    }

    public EditorStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Current limits (settings <c>athanor.max_open_bytes</c>, <c>athanor.max_save_bytes</c>, <c>athanor.save_rate_per_min</c>).</summary>
public interface IAthanorLimits
{
    long MaxOpenBytes { get; }

    long MaxSaveBytes { get; }

    int SaveRatePerMinute { get; }
}

/// <summary>A workspace root on disk: what a resolver hands the directory store.</summary>
/// <param name="Id">The workspace id the client sends.</param>
/// <param name="Kind">Vault or git worktree; decides the write scope.</param>
/// <param name="RootPath">Absolute host-side directory.</param>
/// <param name="ReadOnly">True when saves are refused.</param>
public sealed record EditorWorkspaceRoot(string Id, EditorWorkspaceKind Kind, string RootPath, bool ReadOnly);

/// <summary>Maps a workspace id to a directory on the server (the Vault repo, a clone or an agent worktree).</summary>
public interface IEditorWorkspaceResolver
{
    /// <summary>The workspace root, or null when the id is unknown to this resolver.</summary>
    ValueTask<EditorWorkspaceRoot?> ResolveAsync(string workspaceId, CancellationToken cancellationToken);
}
