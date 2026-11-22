using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Athanor;
using Bazaar.Server.Wire.Connection;
using WireOpenReply = Bazaar.Contracts.Athanor.EditorOpenReply;
using WireOpenRequest = Bazaar.Contracts.Athanor.EditorOpenRequest;
using WireSaveReply = Bazaar.Contracts.Athanor.EditorSaveReply;
using WireSaveRequest = Bazaar.Contracts.Athanor.EditorSaveRequest;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Shared mapping of the Athanor editor handlers onto wire envelopes (doc 57 section 6, PROPOSED host adapter). The module holds no
/// envelope types, so this host glue decodes the payload, calls <c>EditorOpenHandler</c> or <c>EditorSaveHandler</c> and maps the
/// <see cref="EditorResult{T}"/> back to a <see cref="WireReply"/>. Only a human or owner connection may edit; the user id is the
/// connection's principal id, never the payload's. Limits are re-read from settings before each call.
/// </summary>
internal static class AthanorWire
{
    /// <summary>The caller for a connection, or null when the principal may not use the editor (agents and services never do).</summary>
    public static EditorCaller? CallerOf(IWireConnection connection)
    {
        var kind = connection.Principal.Kind;
        var owner = string.Equals(kind, "owner", StringComparison.OrdinalIgnoreCase);
        return owner || string.Equals(kind, "human", StringComparison.OrdinalIgnoreCase)
            ? new EditorCaller(connection.Principal.Id.ToString("D"), owner)
            : null;
    }

    public static WireReply Forbidden() => WireReply.Fail(EditorErrorCodes.Forbidden, "Only a person may use the editor.");

    public static WireReply BadPayload() => WireReply.Fail(WireErrorCodes.BadRequest, "The request does not fit its schema.");

    /// <summary>Maps a failed result to an <c>err</c>. <c>rate_limited</c> carries <c>retryAfterMs</c> (clamped to int).</summary>
    public static WireReply Error<T>(EditorResult<T> result) =>
        WireReply.Fail(
            result.ErrorCode ?? WireErrorCodes.Internal,
            result.Message,
            result.RetryAfterMs is { } ms ? (int)Math.Min(ms, int.MaxValue) : null);
}

/// <summary><c>editor.open</c> over the wire.</summary>
internal sealed class EditorOpenWireHandler(EditorOpenHandler handler, SettingsAthanorLimits limits) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.EditorOpen;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (AthanorWire.CallerOf(context.Connection) is not { } caller)
        {
            return AthanorWire.Forbidden();
        }

        WireOpenRequest request;
        try
        {
            request = context.Payload<WireOpenRequest>();
        }
        catch (WireFormatException)
        {
            return AthanorWire.BadPayload();
        }

        await limits.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var result = await handler.HandleAsync(caller, new EditorOpenRequest(request.WorkspaceId, request.Path), cancellationToken).ConfigureAwait(false);
        return result.IsOk && result.Value is { } v
            ? WireReply.Ok(context.Connection.Codec, new WireOpenReply(v.Content, v.Revision, v.LanguageHint, v.ReadOnly, v.SizeBytes))
            : AthanorWire.Error(result);
    }
}

/// <summary>
/// <c>editor.save</c> over the wire. A conflict is a normal <c>res</c> with <c>serverRevision</c> and <c>serverContent</c> and no
/// <c>newRevision</c>, because the <c>err</c> envelope has no room for them (the first of the two answers the client accepts, doc 57 section 6).
/// </summary>
internal sealed class EditorSaveWireHandler(EditorSaveHandler handler, SettingsAthanorLimits limits) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.EditorSave;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (AthanorWire.CallerOf(context.Connection) is not { } caller)
        {
            return AthanorWire.Forbidden();
        }

        WireSaveRequest request;
        try
        {
            request = context.Payload<WireSaveRequest>();
        }
        catch (WireFormatException)
        {
            return AthanorWire.BadPayload();
        }

        await limits.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var result = await handler.HandleAsync(
            caller, new EditorSaveRequest(request.WorkspaceId, request.Path, request.BaseRevision, request.Content), cancellationToken).ConfigureAwait(false);
        if (result.IsOk && result.Value is { } saved)
        {
            return WireReply.Ok(context.Connection.Codec, new WireSaveReply(saved.NewRevision, null, null));
        }

        if (result.ErrorCode == EditorErrorCodes.Conflict && result.Conflict is { } conflict)
        {
            return WireReply.Ok(context.Connection.Codec, new WireSaveReply(null, conflict.ServerRevision, conflict.ServerContent));
        }

        return AthanorWire.Error(result);
    }
}
