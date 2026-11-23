using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Athanor;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Athanor.Wire;

/// <summary>
/// <see cref="IEditorFileSource"/> over the company server (doc 57 §6, PROPOSED): <c>editor.open</c> and <c>editor.save</c>
/// through <see cref="IWireRequester"/>. The client holds no repository credential. The server decides access, limits and
/// revisions. Cancellation always propagates; every other failure is mapped, never swallowed silently.
/// </summary>
public sealed class WireEditorFileSource(IWireRequester requests) : IEditorFileSource
{
    private const string _defaultLanguage = "plaintext";

    private readonly IWireRequester _requests = requests ?? throw new ArgumentNullException(nameof(requests));

    /// <summary>Loads a document. <c>editor.not_found</c> answers null. Any other refusal throws <see cref="EditorFileException"/>.</summary>
    public async Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        EditorOpenReply? reply;
        try
        {
            reply = await _requests.RequestAsync<EditorOpenRequest, EditorOpenReply>(
                WireTypes.EditorOpen, new EditorOpenRequest(id.WorkspaceId, id.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (WireRequestException ex) when (ex.Error.Code == EditorErrorCodes.NotFound)
        {
            return null;
        }
        catch (WireRequestException ex)
        {
            throw new EditorFileException(ex.Error.Code, Describe(ex.Error), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EditorFileException(EditorErrorCodes.Unavailable, "The server could not be reached.", ex);
        }

        if (reply is null || reply.Content is null || string.IsNullOrEmpty(reply.Revision))
        {
            throw new EditorFileException(EditorErrorCodes.Malformed, "The server's answer to editor.open was incomplete.");
        }

        var language = string.IsNullOrWhiteSpace(reply.LanguageHint) ? _defaultLanguage : reply.LanguageHint;
        return new EditorDocument(id, reply.Content, reply.Revision, language, reply.ReadOnly);
    }

    /// <summary>Saves against <paramref name="baseRevision"/>. A server or transport failure is <see cref="SaveOutcome.Unavailable"/>, never a throw.</summary>
    public async Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(baseRevision);

        EditorSaveReply? reply;
        try
        {
            reply = await _requests.RequestAsync<EditorSaveRequest, EditorSaveReply>(
                WireTypes.EditorSave, new EditorSaveRequest(id.WorkspaceId, id.Path, baseRevision, content), cancellationToken).ConfigureAwait(false);
        }
        catch (WireRequestException ex) when (ex.Error.Code == EditorErrorCodes.Conflict)
        {
            // The error envelope cannot carry the server copy, so fetch it. A failed fetch keeps the draft and says why.
            return await ConflictFromOpenAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (WireRequestException ex)
        {
            return new SaveOutcome.Unavailable(Describe(ex.Error));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SaveOutcome.Unavailable("The server could not be reached.");
        }

        return Map(reply);
    }

    /// <summary>Creates a file in an existing folder. <c>editor.exists</c> is <see cref="CreateOutcome.Exists"/>; every other failure is <see cref="CreateOutcome.Unavailable"/>.</summary>
    public async Task<CreateOutcome> CreateAsync(EditorDocumentId id, string content, CancellationToken cancellationToken)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            var reply = await _requests.RequestAsync<EditorCreateRequest, EditorCreateReply>(
                WireTypes.EditorCreate, new EditorCreateRequest(id.WorkspaceId, id.Path, content), cancellationToken).ConfigureAwait(false);
            return reply is null || string.IsNullOrEmpty(reply.Revision)
                ? new CreateOutcome.Unavailable("The server's answer to editor.create was incomplete.")
                : new CreateOutcome.Created(reply.Revision);
        }
        catch (WireRequestException ex) when (ex.Error.Code == EditorErrorCodes.Exists)
        {
            return new CreateOutcome.Exists();
        }
        catch (WireRequestException ex)
        {
            return new CreateOutcome.Unavailable(Describe(ex.Error));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CreateOutcome.Unavailable("The server could not be reached.");
        }
    }

    private static SaveOutcome Map(EditorSaveReply? reply)
    {
        if (reply is null)
        {
            return new SaveOutcome.Unavailable("The server's answer to editor.save was empty.");
        }

        var accepted = !string.IsNullOrEmpty(reply.NewRevision);
        var conflicted = !string.IsNullOrEmpty(reply.ServerRevision) && reply.ServerContent is not null;
        if (accepted && !conflicted)
        {
            return new SaveOutcome.Saved(reply.NewRevision!);
        }

        if (conflicted && !accepted)
        {
            return new SaveOutcome.Conflict(reply.ServerRevision!, reply.ServerContent!);
        }

        // Neither, or contradictory: the draft must stay, so this is not a save.
        return new SaveOutcome.Unavailable("The server's answer to editor.save was not understood.");
    }

    private async Task<SaveOutcome> ConflictFromOpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        try
        {
            var current = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
            return current is null
                ? new SaveOutcome.Unavailable("The file changed on the server and can no longer be found.")
                : new SaveOutcome.Conflict(current.Revision, current.Content);
        }
        catch (EditorFileException ex)
        {
            return new SaveOutcome.Unavailable($"The file changed on the server and its copy could not be fetched ({ex.Code}).");
        }
    }

    private static string Describe(WireError error) =>
        string.IsNullOrWhiteSpace(error.Message) ? error.Code : $"{error.Code}: {error.Message}";

    private static void ValidateId(EditorDocumentId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Path);
        if (id.Path.Contains('\\') || id.Path.StartsWith('/') || id.Path.Contains('\0'))
        {
            throw new ArgumentException("The path must be relative with forward slashes.", nameof(id));
        }

        foreach (var segment in id.Path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("The path must be normalised.", nameof(id));
            }
        }
    }
}
