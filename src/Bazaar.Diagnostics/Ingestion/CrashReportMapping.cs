using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Diagnostics.Ingestion;

/// <summary>Maps an accepted report onto the inputs of the fingerprint and of the grouping update.</summary>
public static class CrashReportMapping
{
    /// <summary>
    /// Builds the <c>fp1</c> inputs (doc 43 section 8.2). The message comes from <c>message_fingerprint_input</c> when the
    /// device sent one, otherwise from the redacted message, and only matters when there is no error code.
    /// </summary>
    public static FingerprintInput ToFingerprintInput(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var frames = new FingerprintFrame[report.Stack.Count];
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = report.Stack[i];
            frames[i] = new FingerprintFrame(frame.Module, frame.Type, frame.Method, frame.App);
        }

        return new FingerprintInput(report.Error.Code, report.Error.Type, frames, report.Error.MessageFingerprintInput ?? report.Error.Message);
    }

    /// <summary>Builds the grouping occurrence from a report. The device fingerprint is a hint only.</summary>
    public static Occurrence ToOccurrence(CrashReport report) => new(
        ToFingerprintInput(report),
        report.Client.Version,
        report.Device.DeviceClass,
        report.Device.OsFamily,
        report.Fingerprint);
}
