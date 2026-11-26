using System.Text.Json;
using Bazaar.Contracts.Telemetry;
using Bazaar.Contracts.Wire;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>
/// <c>crash.submit</c> (doc 43 section 12.1): binds the report's install id to the authenticated device on first sight, then runs the
/// intake and answers <c>crash.ack</c>. The crash payloads use their own snake_case JSON context (doc 43 section 5.1), so this handler
/// reads the raw JSON payload through <see cref="CrashContractsJsonContext"/> instead of the shared wire context. A refusal is
/// answered with its stable wire name as the error code and no detail. A screenshot may finish before or after its report
/// (<c>crash.attachment.*</c>): a finished one that is held is attached here, and a report whose screenshot is still uploading is
/// held until the screenshot finishes (see <see cref="CrashAttachmentEndHandler"/>).
/// </summary>
public sealed class CrashSubmitHandler(CrashIntakeFactory intake, ICrashInstallRegistry installs, IPendingAttachmentStore pending) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => CrashWireTypes.Submit;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Connection.Codec.Name, "json", StringComparison.Ordinal))
        {
            return WireReply.Fail(WireErrorCodes.Unsupported, "Crash reports need the json codec.");
        }

        CrashSubmit? submit;
        try
        {
            submit = JsonSerializer.Deserialize(context.Request.P.Span, CrashContractsJsonContext.Default.CrashSubmit);
        }
        catch (JsonException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The crash report does not fit its schema.");
        }

        if (submit?.Report is not { } report || !Guid.TryParse(context.Connection.DeviceId, out var deviceId))
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The crash report does not fit its schema.");
        }

        var deviceKey = context.Connection.DeviceId;
        var bound = await installs.TryBindAsync(report.InstallId, deviceId, isShared: false, cancellationToken).ConfigureAwait(false);

        // A screenshot that finished first is taken here. It is taken whether or not this report is accepted, so a refused report
        // cannot leave an upload held behind it.
        IngestionAttachment? upload = null;
        if (report.Screen.AttachmentId is { } named)
        {
            pending.TryTakeUpload(deviceKey, named, out upload);
        }

        var ownership = new BoundInstallOwnership(report.InstallId, bound);
        CrashIntakeResult result;
        try
        {
            result = await intake.Create(ownership)
                .ProcessAsync(new IngestionSubmission(report, upload), cancellationToken).ConfigureAwait(false);
        }
        catch (CrashIntakeContentionException)
        {
            return WireReply.Fail(WireErrorCodes.RateLimited, "Try again shortly.", retryAfterMs: 5000);
        }

        if (result is CrashIntakeAccepted { Duplicate: false } && upload is null && report.Screen.AttachmentId is { } waiting)
        {
            // The screenshot is still uploading: hold the report, so the screenshot attaches when it finishes. If the screenshot finished
            // between the take above and this hold, the hold fails and the finished upload is attached now.
            if (!pending.HoldReport(deviceKey, new PendingReport(waiting, report.ReportId, report.InstallId))
                && pending.TryTakeUpload(deviceKey, waiting, out var finished))
            {
                await intake.Create(ownership).AttachLateAsync(report.InstallId, report.ReportId, finished!, cancellationToken).ConfigureAwait(false);
            }
        }

        return result switch
        {
            CrashIntakeAccepted accepted => WireReply.Ok(JsonSerializer.SerializeToUtf8Bytes(
                new CrashAck(accepted.ReportId, accepted.GroupKey), CrashContractsJsonContext.Default.CrashAck)),
            CrashIntakeRefused refused => WireReply.Fail(WireErrorCodes.BadRequest, refused.Code.ToWireName()),
            _ => WireReply.Fail(WireErrorCodes.Internal),
        };
    }
}
