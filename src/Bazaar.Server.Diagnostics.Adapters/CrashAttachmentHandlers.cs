using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Contracts.Telemetry;
using Bazaar.Contracts.Wire;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>
/// <c>crash.attachment.begin</c> (doc 43 section 5.2): opens a screenshot upload on this connection. The upload is bounded by
/// <see cref="AttachmentUploadAssembler"/>; nothing is stored until the report names it and the upload is complete.
/// </summary>
public sealed class CrashAttachmentBeginHandler(AttachmentUploadAssembler uploads) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => CrashWireTypes.AttachmentBegin;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CrashAttachmentWire.IsJson(context))
        {
            return ValueTask.FromResult(CrashAttachmentWire.NeedsJson());
        }

        if (!CrashAttachmentWire.TryDecode(context, CrashContractsJsonContext.Default.CrashAttachmentBegin, out var begin))
        {
            return ValueTask.FromResult(CrashAttachmentWire.BadPayload());
        }

        var refusal = uploads.Begin(context.Connection.ConnectionId, begin);
        return ValueTask.FromResult(refusal is { } r ? CrashAttachmentWire.Refusal(r) : WireReply.Ok());
    }
}

/// <summary>
/// <c>crash.attachment.chunk</c> (doc 43 section 5.2): one piece of an open screenshot upload. Chunks must arrive in order and within
/// the declared size; anything else is refused and the upload stays as it was.
/// </summary>
public sealed class CrashAttachmentChunkHandler(AttachmentUploadAssembler uploads) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => CrashWireTypes.AttachmentChunk;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CrashAttachmentWire.IsJson(context))
        {
            return ValueTask.FromResult(CrashAttachmentWire.NeedsJson());
        }

        if (!CrashAttachmentWire.TryDecode(context, CrashContractsJsonContext.Default.CrashAttachmentChunk, out var chunk))
        {
            return ValueTask.FromResult(CrashAttachmentWire.BadPayload());
        }

        var refusal = uploads.Chunk(context.Connection.ConnectionId, chunk);
        return ValueTask.FromResult(refusal is { } r ? CrashAttachmentWire.Refusal(r) : WireReply.Ok());
    }
}

/// <summary>
/// <c>crash.attachment.end</c> (doc 43 section 5.2): closes a screenshot upload and attaches it. If its report is already stored and
/// waiting, the screenshot is attached to that report now; otherwise it is held until the report arrives. The hash, the size and the
/// image header are checked by the ingestion pipeline. A screenshot that fails them is refused with its code here, and the report is
/// kept without it.
/// </summary>
public sealed class CrashAttachmentEndHandler(
    AttachmentUploadAssembler uploads,
    IPendingAttachmentStore pending,
    CrashIntakeFactory intake,
    ICrashInstallRegistry installs) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => CrashWireTypes.AttachmentEnd;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CrashAttachmentWire.IsJson(context))
        {
            return CrashAttachmentWire.NeedsJson();
        }

        if (!CrashAttachmentWire.TryDecode(context, CrashContractsJsonContext.Default.CrashAttachmentEnd, out var end))
        {
            return CrashAttachmentWire.BadPayload();
        }

        var (refusal, upload) = uploads.End(context.Connection.ConnectionId, end);
        if (refusal is { } r)
        {
            return CrashAttachmentWire.Refusal(r);
        }

        var deviceKey = context.Connection.DeviceId;
        if (pending.TryTakeReport(deviceKey, end.AttachmentId, out var waiting))
        {
            // The report was stored first. It is attached only when this device still owns its install.
            if (!Guid.TryParse(deviceKey, out var deviceId)
                || !await installs.TryBindAsync(waiting!.InstallId, deviceId, isShared: false, cancellationToken).ConfigureAwait(false))
            {
                return WireReply.Fail(WireErrorCodes.BadRequest, IngestionRefusalCode.InstallNotOwned.ToWireName());
            }

            var late = await intake.Create(new BoundInstallOwnership(waiting.InstallId, bound: true))
                .AttachLateAsync(waiting.InstallId, waiting.ReportId, upload!, cancellationToken).ConfigureAwait(false);
            return late.Attached
                ? WireReply.Ok()
                : WireReply.Fail(WireErrorCodes.BadRequest, late.Code!.Value.ToWireName());
        }

        // The report has not arrived yet. Hold the finished upload for it.
        return pending.HoldUpload(deviceKey, upload!)
            ? WireReply.Ok()
            : WireReply.Fail(WireErrorCodes.RateLimited, "Try again shortly.", retryAfterMs: 5000);
    }
}

/// <summary>The shared JSON decoding and refusal mapping of the three attachment handlers.</summary>
internal static class CrashAttachmentWire
{
    /// <summary>True when the connection uses the JSON codec, which the crash payloads need.</summary>
    public static bool IsJson(WireRequestContext context) => string.Equals(context.Connection.Codec.Name, "json", StringComparison.Ordinal);

    /// <summary>The reply for a connection that does not use JSON.</summary>
    public static WireReply NeedsJson() => WireReply.Fail(WireErrorCodes.Unsupported, "Crash reports need the json codec.");

    /// <summary>The reply for a payload that does not fit its schema.</summary>
    public static WireReply BadPayload() => WireReply.Fail(WireErrorCodes.BadRequest, "The crash attachment does not fit its schema.");

    /// <summary>Decodes the payload with the crash JSON context; a payload that does not decode gives false.</summary>
    public static bool TryDecode<T>(WireRequestContext context, JsonTypeInfo<T> info, out T value)
        where T : class
    {
        try
        {
            var decoded = JsonSerializer.Deserialize(context.Request.P.Span, info);
            if (decoded is null)
            {
                value = null!;
                return false;
            }

            value = decoded;
            return true;
        }
        catch (JsonException)
        {
            value = null!;
            return false;
        }
    }

    /// <summary>
    /// The reply for a refused upload message. Full memory or session limits are retryable, so they answer with the rate-limit code;
    /// everything else is bad input and answers with the refusal's stable wire name.
    /// </summary>
    public static WireReply Refusal(AttachmentUploadRefusal refusal) => refusal switch
    {
        AttachmentUploadRefusal.SessionLimit or AttachmentUploadRefusal.MemoryLimit =>
            WireReply.Fail(WireErrorCodes.RateLimited, "Try again shortly.", retryAfterMs: 5000),
        _ => WireReply.Fail(WireErrorCodes.BadRequest, refusal.ToWireName()),
    };
}
