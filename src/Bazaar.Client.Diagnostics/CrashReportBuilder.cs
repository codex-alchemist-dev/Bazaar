using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Client.Diagnostics;

/// <summary>The device fields the host supplies, because the capture does not read them (doc 43 section 4.1).</summary>
/// <param name="OsFamily">The operating system family, for example <c>windows</c>.</param>
/// <param name="RamBucket">A memory bucket such as <c>16-32 GiB</c>.</param>
/// <param name="DisplayScale">The display scale factor.</param>
/// <param name="DeviceClass">The device class from doc 41: desktop, tablet, foldable or phone.</param>
/// <param name="Posture">The device posture from doc 41.</param>
/// <param name="Locale">The locale tag.</param>
public sealed record CrashDeviceProfile(string OsFamily, string RamBucket, double DisplayScale, string DeviceClass, string Posture, string Locale);

/// <summary>What the host adds to a capture: the install identity, the client head, the release channel and the device profile.</summary>
/// <param name="InstallId">The random install identifier (doc 43 section 5.1).</param>
/// <param name="ClientKind">The client head, for example <c>windows</c>, <c>linux</c> or <c>android</c>.</param>
/// <param name="Channel">The operator's release channel, for example <c>Beta</c>.</param>
/// <param name="Device">The device profile.</param>
public sealed record CrashReportContext(Guid InstallId, string ClientKind, string Channel, CrashDeviceProfile Device);

/// <summary>
/// Turns a finished capture into the wire report of doc 43 section 5.1, and fits it under the size limits of section 5.2.
/// Pure: the same capture, context and identifiers give the same report.
/// </summary>
public static class CrashReportBuilder
{
    /// <summary>
    /// Placeholder for a value the capture does not read yet: the module build id. Doc 43 requires it, but the shared
    /// capture (<see cref="CaptureFrame"/>, <see cref="ModuleInfo"/>) does not carry it. See the module notes.
    /// </summary>
    public const string UnknownValue = "<unknown>";

    /// <summary>The redaction residual state that lets a report be uploaded. Any other value keeps it held.</summary>
    public const string ResidualPass = "pass";

    /// <summary>The residual state of a held report, set when a redaction step failed or the residual scan matched.</summary>
    public const string ResidualFail = "fail";

    private const string _logProblemPrefix = "Log array";
    private const string _reportProblemPrefix = "Report is";

    /// <summary>Builds the report for one capture.</summary>
    /// <param name="capture">The redacted capture from <c>ClientCapture</c>.</param>
    /// <param name="context">The install, client and device information the host supplies.</param>
    /// <param name="settings">The consent settings in force. Their mode and screenshot setting are written into the report.</param>
    /// <param name="reportId">The report identifier, generated on the device.</param>
    /// <param name="createdAt">The report time.</param>
    public static CrashReport Build(CaptureRecord capture, CrashReportContext context, ConsentSettings settings, Guid reportId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var stack = capture.Stack
            .Select(frame => new CrashFrame(frame.Module, UnknownValue, frame.Type, frame.Method, 0, 0, null, null, frame.App))
            .ToArray();
        var modules = capture.Client.Modules
            .Select(module => new CrashModuleInfo(module.Name, module.Version, UnknownValue))
            .ToArray();
        var logs = capture.Logs
            .Select(entry => new CrashLogEntry(entry.Time, entry.Level, entry.Category, entry.Message, null))
            .ToArray();

        return new CrashReport(
            CrashReportSchemas.V1,
            reportId,
            createdAt,
            context.InstallId,
            new CrashClientInfo(context.ClientKind, capture.Client.AppVersion, context.Channel, capture.Client.Device.RuntimeDescription),
            new CrashErrorInfo(capture.ErrorCode, capture.ExceptionType, capture.Message, null),
            stack,
            capture.FlaggedFrame,
            modules,
            logs,
            new CrashDeviceInfo(
                context.Device.OsFamily,
                capture.Client.Device.OsDescription,
                capture.Client.Device.Architecture,
                context.Device.RamBucket,
                context.Device.DisplayScale,
                context.Device.DeviceClass,
                context.Device.Posture,
                context.Device.Locale),
            new CrashScreenInfo(null, new CrashMaskingInfo(0, 0)),
            new CrashRedactionInfo(
                RedactionRules.Version,
                new CrashRedactionCounts(0, 0, 0, 0),
                capture.Held ? ResidualFail : ResidualPass),
            capture.Fingerprint,
            new CrashConsentInfo(
                settings.Mode.ToString().ToLowerInvariant(),
                "queued",
                settings.Screenshot.ToString().ToLowerInvariant()));
    }

    /// <summary>
    /// Drops the oldest log lines until the report is inside the size limits of doc 43 section 5.2, and counts the cut
    /// in <c>redaction.counts.truncated</c>. Returns null when the report cannot fit, or when it fails a check that
    /// trimming cannot fix (the caller then keeps nothing).
    /// </summary>
    public static CrashReport? FitToLimits(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var fitted = report;
        var dropped = false;
        while (true)
        {
            var problems = CrashReportValidation.Validate(fitted);
            if (problems.Count == 0)
            {
                break;
            }

            var sizeOnly = problems.All(p => p.StartsWith(_logProblemPrefix, StringComparison.Ordinal) || p.StartsWith(_reportProblemPrefix, StringComparison.Ordinal));
            if (!sizeOnly || fitted.Logs.Count == 0)
            {
                return null;
            }

            fitted = fitted with { Logs = fitted.Logs.Skip(1).ToArray() };
            dropped = true;
        }

        if (!dropped)
        {
            return report;
        }

        var counts = fitted.Redaction.Counts with { Truncated = fitted.Redaction.Counts.Truncated + 1 };
        return fitted with { Redaction = fitted.Redaction with { Counts = counts } };
    }
}
