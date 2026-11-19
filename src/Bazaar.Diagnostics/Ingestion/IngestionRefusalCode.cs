namespace Bazaar.Diagnostics.Ingestion;

/// <summary>
/// Why the server refused a crash report, or dropped its attachment (doc 43, section 12.1). The wire names are stable:
/// a client shows them, and the audit log records them. Add a code; never rename or reuse one.
/// </summary>
public enum IngestionRefusalCode
{
    /// <summary>The report's install id does not belong to the authenticated device (step 1).</summary>
    InstallNotOwned,

    /// <summary>The schema version is not one this server understands (step 2).</summary>
    UnknownSchema,

    /// <summary>The report JSON is over 256 KiB (section 5.2).</summary>
    ReportTooLarge,

    /// <summary>The <c>logs</c> array is over 64 KiB (section 5.2).</summary>
    LogsTooLarge,

    /// <summary>The report breaks the schema rules: error code shape or flagged frame index (step 2).</summary>
    MalformedReport,

    /// <summary>The server's redaction scan found a secret-shaped or runtime value still in the report (step 3).</summary>
    RedactionResidual,

    /// <summary>The device reported that its own residual scan failed (step 3).</summary>
    RedactionReportedFailed,

    /// <summary>The attachment's id or report id does not match the report that refers to it (step 4).</summary>
    AttachmentNotReferenced,

    /// <summary>The attachment is over the byte cap (step 4).</summary>
    AttachmentTooLarge,

    /// <summary>The bytes received differ from the declared total (step 4).</summary>
    AttachmentSizeMismatch,

    /// <summary>The declared content type is not PNG or JPEG (step 4).</summary>
    AttachmentTypeNotAllowed,

    /// <summary>The SHA-256 of the bytes differs from the declared value (step 4).</summary>
    AttachmentHashMismatch,

    /// <summary>The leading bytes do not match the declared content type (step 4).</summary>
    AttachmentMagicMismatch,

    /// <summary>The image is truncated, has an unsupported structure, or is not a valid header (step 4).</summary>
    AttachmentMalformed,

    /// <summary>The image width or height is over the dimension cap (step 4).</summary>
    AttachmentDimensionsExceeded,

    /// <summary>The image carries metadata or chunks beyond what the pixels need (step 4).</summary>
    AttachmentMetadataPresent,
}

/// <summary>The stable wire names of <see cref="IngestionRefusalCode"/>.</summary>
public static class IngestionRefusalCodes
{
    /// <summary>Returns the stable snake_case name that is written to the audit log and shown to clients.</summary>
    public static string ToWireName(this IngestionRefusalCode code) => code switch
    {
        IngestionRefusalCode.InstallNotOwned => "install_not_owned",
        IngestionRefusalCode.UnknownSchema => "unknown_schema",
        IngestionRefusalCode.ReportTooLarge => "report_too_large",
        IngestionRefusalCode.LogsTooLarge => "logs_too_large",
        IngestionRefusalCode.MalformedReport => "malformed_report",
        IngestionRefusalCode.RedactionResidual => "redaction_residual",
        IngestionRefusalCode.RedactionReportedFailed => "redaction_reported_failed",
        IngestionRefusalCode.AttachmentNotReferenced => "attachment_not_referenced",
        IngestionRefusalCode.AttachmentTooLarge => "attachment_too_large",
        IngestionRefusalCode.AttachmentSizeMismatch => "attachment_size_mismatch",
        IngestionRefusalCode.AttachmentTypeNotAllowed => "attachment_type_not_allowed",
        IngestionRefusalCode.AttachmentHashMismatch => "attachment_hash_mismatch",
        IngestionRefusalCode.AttachmentMagicMismatch => "attachment_magic_mismatch",
        IngestionRefusalCode.AttachmentMalformed => "attachment_malformed",
        IngestionRefusalCode.AttachmentDimensionsExceeded => "attachment_dimensions_exceeded",
        IngestionRefusalCode.AttachmentMetadataPresent => "attachment_metadata_present",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown refusal code."),
    };
}
