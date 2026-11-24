namespace Bazaar.Server.Athanor;

/// <summary>
/// <c>editor.open</c> (doc 57 section 6). The host adapter decodes the envelope payload, calls this and maps the result to a
/// <c>res</c> or <c>err</c> envelope. This module does not reference Bazaar.Server, so it holds no envelope types.
/// </summary>
public sealed class EditorOpenHandler
{
    private readonly IEditorWorkspaceStore _store;
    private readonly IEditorAuthorizer _authorizer;
    private readonly IAthanorLimits _limits;
    private readonly IEditorPathPolicy _policy;

    public EditorOpenHandler(IEditorWorkspaceStore store, IEditorAuthorizer authorizer, IAthanorLimits limits, IEditorPathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(limits);
        _store = store;
        _authorizer = authorizer;
        _limits = limits;
        _policy = policy ?? AllowAllEditorPathPolicy.Instance;
    }

    public const string Type = EditorWireTypes.Open;

    public async ValueTask<EditorResult<EditorOpenReply>> HandleAsync(EditorCaller caller, EditorOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.BadRequest, "A workspace id is required.");
        }

        if (!EditorPath.TryNormalize(request.Path, out var path, out var pathError))
        {
            return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.BadRequest, pathError);
        }

        try
        {
            var workspace = await _store.GetWorkspaceAsync(request.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (workspace is null)
            {
                return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.NotFound, "The workspace does not exist.");
            }

            if (!await _authorizer.IsAllowedAsync(caller, workspace, EditorAccess.Read, cancellationToken).ConfigureAwait(false))
            {
                return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.Forbidden, "You may not open files in this workspace.");
            }

            if (!await _policy.IsAllowedAsync(caller, workspace, path, EditorAccess.Read, cancellationToken).ConfigureAwait(false))
            {
                return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.Forbidden, "That path may not be opened.");
            }

            var read = await _store.ReadAsync(workspace.Id, path, _limits.MaxOpenBytes, cancellationToken).ConfigureAwait(false);
            switch (read.Status)
            {
                case EditorReadStatus.NotFound:
                    return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.NotFound, "The file does not exist.");
                case EditorReadStatus.Blocked:
                    return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.Forbidden, "That path may not be opened.");
                case EditorReadStatus.TooLarge:
                    return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.TooLarge, "The file is too large to open.");
            }

            var bytes = read.Content ?? [];
            if (bytes.LongLength > _limits.MaxOpenBytes)
            {
                return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.TooLarge, "The file is too large to open.");
            }

            if (EditorText.IsBinary(bytes))
            {
                return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.Binary, "The file is not text.");
            }

            var canWrite = !workspace.ReadOnly
                && await _policy.IsAllowedAsync(caller, workspace, path, EditorAccess.Write, cancellationToken).ConfigureAwait(false)
                && await _authorizer.IsAllowedAsync(caller, workspace, EditorAccess.Write, cancellationToken).ConfigureAwait(false);
            return EditorResult.Ok<EditorOpenReply>(new EditorOpenReply(
                EditorText.Decode(bytes), read.Revision ?? string.Empty, EditorText.LanguageHint(path), !canWrite, bytes.LongLength));
        }
        catch (EditorStoreUnavailableException)
        {
            return EditorResult.Fail<EditorOpenReply>(EditorErrorCodes.Unavailable, "The workspace is not available right now.");
        }
    }
}
