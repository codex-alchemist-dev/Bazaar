using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Athanor;

/// <summary>
/// <c>editor.save</c> (doc 57 section 6): a revision-checked save of the caller's own edit. Never merges or pushes. Each accepted save
/// appends one audit row (user, workspace, path, base and new revision, byte count). If the audit append throws after the write, the
/// exception propagates to the host (an <c>internal</c> error); the file is already written.
/// </summary>
public sealed class EditorSaveHandler
{
    /// <summary>Audit kind of an accepted save.</summary>
    public const string AuditKind = "editor.save";

    private readonly IEditorWorkspaceStore _store;
    private readonly IEditorAuthorizer _authorizer;
    private readonly IAthanorLimits _limits;
    private readonly SaveRateLimiter _rate;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly IEditorPathPolicy _policy;

    public EditorSaveHandler(
        IEditorWorkspaceStore store, IEditorAuthorizer authorizer, IAthanorLimits limits, SaveRateLimiter rate, IAuditLog audit, TimeProvider time,
        IEditorPathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _authorizer = authorizer;
        _limits = limits;
        _rate = rate;
        _audit = audit;
        _time = time;
        _policy = policy ?? AllowAllEditorPathPolicy.Instance;
    }

    public const string Type = EditorWireTypes.Save;

    public async ValueTask<EditorResult<EditorSaveReply>> HandleAsync(EditorCaller caller, EditorSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrEmpty(request.BaseRevision) || request.Content is null)
        {
            return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.BadRequest, "A workspace id, base revision and content are required.");
        }

        if (!EditorPath.TryNormalize(request.Path, out var path, out var pathError))
        {
            return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.BadRequest, pathError);
        }

        try
        {
            var workspace = await _store.GetWorkspaceAsync(request.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (workspace is null)
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.NotFound, "The workspace does not exist.");
            }

            if (workspace.ReadOnly)
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.ReadOnlyWorkspace, "The workspace is read-only.");
            }

            if (!await _authorizer.IsAllowedAsync(caller, workspace, EditorAccess.Write, cancellationToken).ConfigureAwait(false))
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.Forbidden, "You may not save files in this workspace.");
            }

            if (!await _policy.IsAllowedAsync(caller, workspace, path, EditorAccess.Write, cancellationToken).ConfigureAwait(false))
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.Forbidden, "That path may not be saved.");
            }

            if (!_rate.TryAcquire(caller.UserId, out var retryAfterMs))
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.RateLimited, "Too many saves. Try again shortly.", retryAfterMs);
            }

            var size = EditorText.ByteCount(request.Content);
            if (size > _limits.MaxSaveBytes)
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.TooLarge, "The content is too large to save.");
            }

            if (EditorText.IsBinary(request.Content))
            {
                return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.Binary, "The content is not text.");
            }

            var written = await _store.WriteAsync(workspace.Id, path, request.BaseRevision, Encoding.UTF8.GetBytes(request.Content), cancellationToken)
                .ConfigureAwait(false);
            switch (written.Status)
            {
                case EditorWriteStatus.NotFound:
                    return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.NotFound, "The file does not exist.");
                case EditorWriteStatus.Blocked:
                    return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.Forbidden, "That path may not be saved.");
                case EditorWriteStatus.TooLarge:
                    return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.TooLarge, "The file on the server is too large to compare.");
                case EditorWriteStatus.Conflict:
                    return EditorResult.Fail<EditorSaveReply>(
                        EditorErrorCodes.Conflict,
                        "The file changed on the server.",
                        conflict: new EditorConflictDetail(written.ServerRevision ?? string.Empty, EditorText.Decode(written.ServerContent ?? [])));
            }

            var newRevision = written.NewRevision ?? string.Empty;
            await _audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), caller.UserId, AuditKind, AuditPayload(workspace.Id, path, request.BaseRevision, newRevision, size)),
                cancellationToken).ConfigureAwait(false);
            return EditorResult.Ok<EditorSaveReply>(new EditorSaveReply(newRevision));
        }
        catch (EditorStoreUnavailableException)
        {
            return EditorResult.Fail<EditorSaveReply>(EditorErrorCodes.Unavailable, "The workspace is not available right now.");
        }
    }

    private static string AuditPayload(string workspaceId, string path, string baseRevision, string newRevision, long bytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("workspaceId", workspaceId);
            writer.WriteString("path", path);
            writer.WriteString("baseRevision", baseRevision);
            writer.WriteString("newRevision", newRevision);
            writer.WriteNumber("bytes", bytes);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
