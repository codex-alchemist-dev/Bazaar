using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Ingestion;

/// <summary>
/// The checks a crash report passes before it is stored (doc 43 section 12.1, steps 1 to 6), in that order. The first
/// refusal ends the run, so nothing after it executes. Storing the report, upserting the group and filing (steps 7
/// and 8) happen outside, on the <see cref="IngestionAccepted"/> result. An attachment that fails its checks is dropped
/// and the report is kept (section 5.2).
/// </summary>
public sealed class CrashIngestionPipeline
{
    private readonly IInstallOwnershipResolver _ownership;
    private readonly Redactor _redactor;
    private readonly ISymbolicator _symbolicator;

    /// <summary>Creates a pipeline over the seams the caller supplies.</summary>
    /// <param name="ownership">Step 1: whether the install id belongs to the authenticated device.</param>
    /// <param name="redactor">Step 3: the server-side redaction validator, built with the server's rules and exact values.</param>
    /// <param name="symbolicator">Step 6: the release symbol bundle lookup.</param>
    public CrashIngestionPipeline(IInstallOwnershipResolver ownership, Redactor redactor, ISymbolicator symbolicator)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(symbolicator);
        _ownership = ownership;
        _redactor = redactor;
        _symbolicator = symbolicator;
    }

    /// <summary>Runs steps 1 to 6 on one submission. It never throws for bad input; a bad report is a refusal.</summary>
    public IngestionOutcome Process(IngestionSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var report = submission.Report;

        if (!_ownership.OwnsInstall(report.InstallId))
        {
            return Refuse(IngestionStep.InstallOwnership, IngestionRefusalCode.InstallNotOwned, "The install id is not registered to the authenticated device.");
        }

        var schema = CheckSchemaAndSize(report);
        if (schema.Refusal is { } schemaRefusal)
        {
            return Refuse(IngestionStep.SchemaAndSize, schemaRefusal, schema.Detail!);
        }

        var redaction = CheckRedaction(report);
        if (redaction.Refusal is { } redactionRefusal)
        {
            return Refuse(IngestionStep.Redaction, redactionRefusal, redaction.Detail!);
        }

        var attachment = submission.Attachment is null
            ? null
            : CheckAttachment(report.ReportId, report.Screen.AttachmentId, submission.Attachment);

        var fingerprint = Fingerprint.Compute(CrashReportMapping.ToFingerprintInput(report));
        var mismatch = report.Fingerprint is { } hint && !string.Equals(hint, fingerprint, StringComparison.Ordinal);

        return new IngestionAccepted(fingerprint, mismatch, _symbolicator.Resolve(report), attachment);
    }

    private static IngestionRefused Refuse(IngestionStep step, IngestionRefusalCode code, string detail) => new(step, code, detail);

    private static StepVerdict CheckSchemaAndSize(CrashReport report)
    {
        if (!string.Equals(report.Schema, CrashReportSchemas.V1, StringComparison.Ordinal))
        {
            return StepVerdict.Refuse(IngestionRefusalCode.UnknownSchema, $"Schema '{report.Schema}' is not supported.");
        }

        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, CrashContractsJsonContext.Default.CrashReport).Length;
        if (reportBytes > CrashReportLimits.MaxReportBytes)
        {
            return StepVerdict.Refuse(IngestionRefusalCode.ReportTooLarge, $"Report is {reportBytes} bytes.");
        }

        var logsBytes = JsonSerializer.SerializeToUtf8Bytes(report.Logs, CrashContractsJsonContext.Default.IReadOnlyListCrashLogEntry).Length;
        if (logsBytes > CrashReportLimits.MaxLogsBytes)
        {
            return StepVerdict.Refuse(IngestionRefusalCode.LogsTooLarge, $"Logs are {logsBytes} bytes.");
        }

        var problems = CrashReportValidation.Validate(report);
        return problems.Count == 0
            ? StepVerdict.Pass
            : StepVerdict.Refuse(IngestionRefusalCode.MalformedReport, problems[0]);
    }

    private StepVerdict CheckRedaction(CrashReport report)
    {
        if (!string.Equals(report.Redaction.ResidualScan, "pass", StringComparison.Ordinal))
        {
            return StepVerdict.Refuse(IngestionRefusalCode.RedactionReportedFailed, $"The device residual scan reported '{report.Redaction.ResidualScan}'.");
        }

        try
        {
            foreach (var (name, text) in FreeTextFields(report))
            {
                if (_redactor.HasResidual(text))
                {
                    return StepVerdict.Refuse(IngestionRefusalCode.RedactionResidual, $"Field '{name}' still matches a redaction rule or an exact value.");
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: a scan that cannot finish is a scan that did not pass.
            return StepVerdict.Refuse(IngestionRefusalCode.RedactionResidual, "The residual scan timed out.");
        }

        return StepVerdict.Pass;
    }

    private static IEnumerable<(string Name, string Text)> FreeTextFields(CrashReport report)
    {
        yield return ("error.message", report.Error.Message);
        if (report.Error.MessageFingerprintInput is { } fingerprintInput)
        {
            yield return ("error.message_fingerprint_input", fingerprintInput);
        }

        for (var i = 0; i < report.Logs.Count; i++)
        {
            var log = report.Logs[i];
            yield return ($"logs[{i}].msg", log.Msg);
            if (log.Props is { } props)
            {
                if (props.Status is { } status)
                {
                    yield return ($"logs[{i}].props.status", status);
                }

                if (props.Module is { } module)
                {
                    yield return ($"logs[{i}].props.module", module);
                }

                if (props.ErrorCode is { } errorCode)
                {
                    yield return ($"logs[{i}].props.error_code", errorCode);
                }
            }
        }

        for (var i = 0; i < report.Stack.Count; i++)
        {
            if (report.Stack[i].File is { } file)
            {
                yield return ($"stack[{i}].file", file);
            }
        }
    }

    /// <summary>
    /// The attachment checks of step 4 for a report that names <paramref name="referencedAttachmentId"/>. The intake uses it both when
    /// the attachment came with the report and when it finished after the report was stored, so both paths run the same checks.
    /// </summary>
    /// <param name="reportId">The id of the report the attachment belongs to.</param>
    /// <param name="referencedAttachmentId">The attachment id the report names, or null when it names none.</param>
    /// <param name="attachment">The assembled attachment.</param>
    public static AttachmentDecision CheckAttachment(Guid reportId, Guid? referencedAttachmentId, IngestionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var begin = attachment.Begin;
        if (referencedAttachmentId != begin.AttachmentId
            || begin.ReportId != reportId
            || attachment.End.AttachmentId != begin.AttachmentId)
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentNotReferenced, "The attachment ids do not match the report.");
        }

        var data = attachment.Data.Span;
        if (begin.TotalBytes > IngestionLimits.MaxAttachmentBytes || data.Length > IngestionLimits.MaxAttachmentBytes)
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentTooLarge, $"Attachment is over {IngestionLimits.MaxAttachmentBytes} bytes.");
        }

        if (begin.TotalBytes != data.Length)
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentSizeMismatch, $"Declared {begin.TotalBytes} bytes, received {data.Length}.");
        }

        if (ImageHeaders.FormatOf(begin.ContentType) is not { } declared)
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentTypeNotAllowed, $"Content type '{begin.ContentType}' is not allowed.");
        }

        if (CrashReportValidation.ValidateAttachmentEnd(attachment.End).Count > 0
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), attachment.End.Sha256, StringComparison.Ordinal))
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentHashMismatch, "The SHA-256 of the bytes differs from the declared value.");
        }

        if (ImageHeaders.DetectFormat(data) != declared)
        {
            return AttachmentDecision.Drop(IngestionRefusalCode.AttachmentMagicMismatch, "The leading bytes do not match the declared content type.");
        }

        var header = ImageHeaders.Read(data, declared);
        return header.Refusal is { } refusal
            ? AttachmentDecision.Drop(refusal, "The image header was refused.")
            : AttachmentDecision.Keep;
    }
}
