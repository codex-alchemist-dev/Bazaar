using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Bazaar.Server.Athanor;

/// <summary>
/// In-memory <see cref="IEditorWorkspaceStore"/> for tests and offline development. Revisions are the SHA-256 of the content, like a
/// git blob id. Set <see cref="Unavailable"/> to make every call throw <see cref="EditorStoreUnavailableException"/>.
/// </summary>
public sealed class InMemoryEditorWorkspaceStore : IEditorWorkspaceStore
{
    private readonly ConcurrentDictionary<string, EditorWorkspaceInfo> _workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Workspace, string Path), byte[]> _files = new();
    private readonly Lock _gate = new();

    /// <summary>When true, every call throws <see cref="EditorStoreUnavailableException"/>.</summary>
    public bool Unavailable { get; set; }

    public void AddWorkspace(EditorWorkspaceInfo workspace) => _workspaces[workspace.Id] = workspace;

    public void PutFile(string workspaceId, string path, byte[] content)
    {
        lock (_gate)
        {
            _files[(workspaceId, path)] = content;
        }
    }

    /// <summary>The stored bytes of a file, or null.</summary>
    public byte[]? GetFile(string workspaceId, string path) => _files.TryGetValue((workspaceId, path), out var bytes) ? bytes : null;

    /// <summary>The revision of the given content.</summary>
    public static string RevisionOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public ValueTask<EditorWorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return ValueTask.FromResult(_workspaces.TryGetValue(workspaceId, out var w) ? w : null);
    }

    public ValueTask<EditorReadResult> ReadAsync(string workspaceId, string path, long maxBytes, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        byte[]? bytes;
        lock (_gate)
        {
            _files.TryGetValue((workspaceId, path), out bytes);
        }

        if (bytes is null)
        {
            return ValueTask.FromResult(new EditorReadResult(EditorReadStatus.NotFound));
        }

        return ValueTask.FromResult(bytes.LongLength > maxBytes
            ? new EditorReadResult(EditorReadStatus.TooLarge, bytes.LongLength)
            : new EditorReadResult(EditorReadStatus.Ok, bytes.LongLength, RevisionOf(bytes), bytes));
    }

    public ValueTask<EditorWriteResult> WriteAsync(string workspaceId, string path, string baseRevision, byte[] content, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        lock (_gate)
        {
            if (!_files.TryGetValue((workspaceId, path), out var current))
            {
                return ValueTask.FromResult(new EditorWriteResult(EditorWriteStatus.NotFound));
            }

            var currentRevision = RevisionOf(current);
            if (!string.Equals(currentRevision, baseRevision, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new EditorWriteResult(EditorWriteStatus.Conflict, ServerRevision: currentRevision, ServerContent: current));
            }

            _files[(workspaceId, path)] = content;
            return ValueTask.FromResult(new EditorWriteResult(EditorWriteStatus.Written, NewRevision: RevisionOf(content)));
        }
    }

    public ValueTask<EditorCreateResult> CreateAsync(string workspaceId, string path, byte[] content, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (!_workspaces.TryGetValue(workspaceId, out var workspace) || workspace.ReadOnly)
        {
            return ValueTask.FromResult(new EditorCreateResult(EditorCreateStatus.Blocked));
        }

        lock (_gate)
        {
            if (_files.ContainsKey((workspaceId, path)))
            {
                return ValueTask.FromResult(new EditorCreateResult(EditorCreateStatus.Exists));
            }

            var slash = path.LastIndexOf('/');
            if (slash > 0 && !_files.Keys.Any(k => k.Workspace == workspaceId && k.Path.StartsWith(path[..(slash + 1)], StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(new EditorCreateResult(EditorCreateStatus.ParentMissing));
            }

            _files[(workspaceId, path)] = content;
            return ValueTask.FromResult(new EditorCreateResult(EditorCreateStatus.Created, RevisionOf(content)));
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new EditorStoreUnavailableException("The in-memory store is marked unavailable.");
        }
    }
}
