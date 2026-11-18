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
/// answered with its stable wire name as the error code and no detail. Screenshot upload (<c>crash.attachment.*</c>) is not handled
/// yet: a report that names one is stored without it.
/// </summary>
public sealed class CrashSubmitHandler(CrashIntakeFactory intake, ICrashInstallRegistry installs) : IWireRequestHandler
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

        var bound = await installs.TryBindAsync(report.InstallId, deviceId, isShared: false, cancellationToken).ConfigureAwait(false);
        CrashIntakeResult result;
        try
        {
            result = await intake.Create(new BoundOwnership(report.InstallId, bound))
                .ProcessAsync(new IngestionSubmission(report), cancellationToken).ConfigureAwait(false);
        }
        catch (CrashIntakeContentionException)
        {
            return WireReply.Fail(WireErrorCodes.RateLimited, "Try again shortly.", retryAfterMs: 5000);
        }

        return result switch
        {
            CrashIntakeAccepted accepted => WireReply.Ok(JsonSerializer.SerializeToUtf8Bytes(
                new CrashAck(accepted.ReportId, accepted.GroupKey), CrashContractsJsonContext.Default.CrashAck)),
            CrashIntakeRefused refused => WireReply.Fail(WireErrorCodes.BadRequest, refused.Code.ToWireName()),
            _ => WireReply.Fail(WireErrorCodes.Internal),
        };
    }

    private sealed class BoundOwnership(Guid installId, bool bound) : IInstallOwnershipResolver
    {
        public bool OwnsInstall(Guid candidate) => bound && candidate == installId;
    }
}
