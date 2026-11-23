using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Athanor;

/// <summary>
/// <c>editor.create</c> (doc 57 section 8, PROPOSED): a new file in an existing folder of a workspace the server controls. Needs the same
/// write scope, path policy, size limit and per-user rate limit as a save, never replaces a file and creates no folder. Each created file
/// appends one audit row (user, workspace, path, revision, byte count).
/// </summary>
public sealed class EditorCreateHandler
{
    /// <summary>Audit kind of a created file.</summary>
    public const string AuditKind = "editor.create";

    private readonly IEditorWorkspaceStore _store;
    private readonly IEditorAuthorizer _authorizer;
    private readonly IAthanorLimits _limits;
    private readonly SaveRateLimiter _rate;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly IEditorPathPolicy _policy;

    public EditorCreateHandler(
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

    public const string Type = EditorWireTypes.Create;

    public async ValueTask<EditorResult<EditorCreateReply>> HandleAsync(EditorCaller caller, EditorCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || request.Content is null)
        {
            return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.BadRequest, "A workspace id and content are required.");
        }

        if (!EditorPath.TryNormalize(request.Path, out var path, out var pathError))
        {
            return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.BadRequest, pathError);
        }

        try
        {
            var workspace = await _store.GetWorkspaceAsync(request.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (workspace is null)
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.NotFound, "The workspace does not exist.");
            }

            if (workspace.ReadOnly)
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.ReadOnlyWorkspace, "The workspace is read-only.");
            }

            if (!await _authorizer.IsAllowedAsync(caller, workspace, EditorAccess.Write, cancellationToken).ConfigureAwait(false)
                || !await _policy.IsAllowedAsync(caller, workspace, path, EditorAccess.Write, cancellationToken).ConfigureAwait(false))
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.Forbidden, "You may not create files here.");
            }

            if (!_rate.TryAcquire(caller.UserId, out var retryAfterMs))
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.RateLimited, "Too many saves. Try again shortly.", retryAfterMs);
            }

            var size = EditorText.ByteCount(request.Content);
            if (size > _limits.MaxSaveBytes)
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.TooLarge, "The content is too large to save.");
            }

            if (EditorText.IsBinary(request.Content))
            {
                return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.Binary, "The content is not text.");
            }

            var created = await _store.CreateAsync(workspace.Id, path, Encoding.UTF8.GetBytes(request.Content), cancellationToken).ConfigureAwait(false);
            switch (created.Status)
            {
                case EditorCreateStatus.Exists:
                    return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.Exists, "A file already exists at that path.");
                case EditorCreateStatus.ParentMissing:
                    return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.NotFound, "The folder does not exist.");
                case EditorCreateStatus.Blocked:
                    return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.Forbidden, "That path may not be created.");
            }

            var revision = created.Revision ?? string.Empty;
            await _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), caller.UserId, AuditKind, AuditPayload(workspace.Id, path, revision, size)), cancellationToken)
                .ConfigureAwait(false);
            return EditorResult.Ok<EditorCreateReply>(new EditorCreateReply(revision));
        }
        catch (EditorStoreUnavailableException)
        {
            return EditorResult.Fail<EditorCreateReply>(EditorErrorCodes.Unavailable, "The workspace is not available right now.");
        }
    }

    private static string AuditPayload(string workspaceId, string path, string revision, long bytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("workspaceId", workspaceId);
            writer.WriteString("path", path);
            writer.WriteString("revision", revision);
            writer.WriteNumber("bytes", bytes);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
