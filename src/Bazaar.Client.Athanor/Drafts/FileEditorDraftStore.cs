using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Athanor.Drafts;

/// <summary>
/// Keeps each draft as one JSON file under <c>rootDirectory</c> (doc 57 §5, doc 28 renderer crash recovery).
/// The file name is the SHA-256 hex of "workspaceId\npath", so a raw path never becomes a file name and cannot escape the root.
/// Writes go to a temp file in the same folder and then replace the target, so a crash leaves either the old or the new draft.
/// Corrupt, truncated or foreign files are skipped on read and list. The draft key is the workspace id and path only.
/// <see cref="EditorDraft.SavedAt"/> is stamped from <paramref name="clock"/> on write.
/// </summary>
public sealed class FileEditorDraftStore : IEditorDraftStore
{
    private const string _draftExtension = ".json";
    private const string _tempExtension = ".tmp";

    private readonly string _rootDirectory;
    private readonly TimeProvider _clock;

    public FileEditorDraftStore(string rootDirectory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _clock = clock;
    }

    public async Task WriteAsync(EditorDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);

        var target = FilePathFor(draft.Id);
        var temp = Path.Combine(_rootDirectory, $"{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}{_tempExtension}");
        var file = new EditorDraftFile(draft.Id.WorkspaceId, draft.Id.Path, draft.Content, draft.BaseRevision, _clock.GetUtcNow());
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, file, EditorDraftJsonContext.Default.EditorDraftFile, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            // Only a failed or cancelled write leaves its temp file behind; a successful move has already consumed it.
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async Task<EditorDraft?> ReadAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await ReadFileAsync(FilePathFor(id), cancellationToken);
        if (file is null || file.WorkspaceId != id.WorkspaceId || file.Path != id.Path)
        {
            return null;
        }

        return ToDraft(file);
    }

    public Task DeleteAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = FilePathFor(id);
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<EditorDraft>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        var drafts = new List<EditorDraft>();
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*" + _draftExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await ReadFileAsync(path, cancellationToken);
            if (file is not null && FilePathFor(new EditorDocumentId(file.WorkspaceId, file.Path)) == path)
            {
                drafts.Add(ToDraft(file));
            }
        }

        drafts.Sort(static (a, b) =>
        {
            var byTime = a.SavedAt.CompareTo(b.SavedAt);
            if (byTime != 0)
            {
                return byTime;
            }

            var byWorkspace = string.CompareOrdinal(a.Id.WorkspaceId, b.Id.WorkspaceId);
            return byWorkspace != 0 ? byWorkspace : string.CompareOrdinal(a.Id.Path, b.Id.Path);
        });
        return drafts;
    }

    private string FilePathFor(EditorDocumentId id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id.WorkspaceId}\n{id.Path}"));
        return Path.Combine(_rootDirectory, Convert.ToHexStringLower(bytes) + _draftExtension);
    }

    // Returns null for a missing, corrupt or truncated file. Other I/O failures still throw.
    private static async Task<EditorDraftFile?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var file = await JsonSerializer.DeserializeAsync(stream, EditorDraftJsonContext.Default.EditorDraftFile, cancellationToken);
            return file is { WorkspaceId: not null, Path: not null, Content: not null, BaseRevision: not null } ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EditorDraft ToDraft(EditorDraftFile file) =>
        new(new EditorDocumentId(file.WorkspaceId, file.Path), file.Content, file.BaseRevision, file.SavedAt);
}
