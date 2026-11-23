namespace Bazaar.Server.Athanor;

/// <summary>Wire type names of the Athanor editor pair (doc 57 section 6).</summary>
public static class EditorWireTypes
{
    /// <summary>Open a file for editing. Request <see cref="EditorOpenRequest"/>, reply <see cref="EditorOpenReply"/>.</summary>
    public const string Open = "editor.open";

    /// <summary>Save a file with a revision check. Request <see cref="EditorSaveRequest"/>, reply <see cref="EditorSaveReply"/>.</summary>
    public const string Save = "editor.save";

    /// <summary>Create a new file (doc 57 section 8, PROPOSED). Request <see cref="EditorCreateRequest"/>, reply <see cref="EditorCreateReply"/>.</summary>
    public const string Create = "editor.create";
}

/// <summary>
/// Error codes of the editor pair (doc 57 section 6). The last two are PROPOSED additions where the doc names no code:
/// a malformed path or payload uses the envelope's <c>bad_request</c>, and a rate limit uses the envelope's <c>rate_limited</c>.
/// </summary>
public static class EditorErrorCodes
{
    public const string NotFound = "editor.not_found";
    public const string Conflict = "editor.conflict";
    public const string Forbidden = "editor.forbidden";
    public const string TooLarge = "editor.too_large";
    public const string Binary = "editor.binary";
    public const string ReadOnlyWorkspace = "editor.read_only_workspace";
    public const string Unavailable = "editor.unavailable";

    /// <summary>PROPOSED (doc 57 section 8): a create over a file that already exists.</summary>
    public const string Exists = "editor.exists";

    /// <summary>Envelope code for a path or payload that fails validation (same value as <c>WireErrorCodes.BadRequest</c>).</summary>
    public const string BadRequest = "bad_request";

    /// <summary>Envelope code for the per-user save rate limit (same value as <c>WireErrorCodes.RateLimited</c>).</summary>
    public const string RateLimited = "rate_limited";
}

/// <summary>Payload of <c>editor.open</c>.</summary>
public sealed record EditorOpenRequest(string WorkspaceId, string Path);

/// <summary>Reply to <c>editor.open</c>.</summary>
/// <param name="Content">The file text (UTF-8).</param>
/// <param name="Revision">Opaque server revision; send it back as <c>baseRevision</c> on save.</param>
/// <param name="LanguageHint">Language id guessed from the extension, or null.</param>
/// <param name="ReadOnly">True when the workspace is read-only or the caller lacks the write scope.</param>
/// <param name="SizeBytes">UTF-8 size of the content.</param>
public sealed record EditorOpenReply(string Content, string Revision, string? LanguageHint, bool ReadOnly, long SizeBytes);

/// <summary>Payload of <c>editor.save</c>.</summary>
public sealed record EditorSaveRequest(string WorkspaceId, string Path, string BaseRevision, string Content);

/// <summary>Reply to <c>editor.save</c>.</summary>
public sealed record EditorSaveReply(string NewRevision);

/// <summary>Payload of <c>editor.create</c>: a new file in an existing folder of the workspace.</summary>
public sealed record EditorCreateRequest(string WorkspaceId, string Path, string Content);

/// <summary>Reply to <c>editor.create</c>.</summary>
public sealed record EditorCreateReply(string Revision);

/// <summary>Extra detail carried by an <c>editor.conflict</c> error.</summary>
public sealed record EditorConflictDetail(string ServerRevision, string ServerContent);

/// <summary>The signed-in human making the call. <paramref name="IsOwner"/> is true for the owner principal, false for a member.</summary>
public sealed record EditorCaller(string UserId, bool IsOwner = false);

/// <summary>
/// Per-path read and write rules on top of the workspace scope (<see cref="IEditorAuthorizer"/>), for example the Vault's area and owner
/// rules (doc 18, "Access"). A refusal answers <c>editor.forbidden</c> before the file is read, so a refused path reveals nothing.
/// </summary>
public interface IEditorPathPolicy
{
    ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, string path, EditorAccess access, CancellationToken cancellationToken);
}

/// <summary>Outcome of a handler call: a value, or an error code with a message. Maps to a <c>res</c> or <c>err</c> envelope.</summary>
public sealed record EditorResult<T>
{
    public T? Value { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    /// <summary>Set on <c>rate_limited</c>.</summary>
    public long? RetryAfterMs { get; init; }

    /// <summary>Set on <c>editor.conflict</c>.</summary>
    public EditorConflictDetail? Conflict { get; init; }

    public bool IsOk => ErrorCode is null;
}

/// <summary>Factories for <see cref="EditorResult{T}"/>.</summary>
public static class EditorResult
{
    public static EditorResult<T> Ok<T>(T value) => new() { Value = value };

    public static EditorResult<T> Fail<T>(string code, string message, long? retryAfterMs = null, EditorConflictDetail? conflict = null) =>
        new() { ErrorCode = code, Message = message, RetryAfterMs = retryAfterMs, Conflict = conflict };
}
