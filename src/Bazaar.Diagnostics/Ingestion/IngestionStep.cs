namespace Bazaar.Diagnostics.Ingestion;

/// <summary>The steps of the ingestion pipeline, in the order of doc 43 section 12.1. Steps 7 and 8 are out of scope here.</summary>
public enum IngestionStep
{
    /// <summary>Step 1: the install id belongs to the authenticated device.</summary>
    InstallOwnership,

    /// <summary>Step 2: schema version, size caps and the structural rules of the report.</summary>
    SchemaAndSize,

    /// <summary>Step 3: the server-side redaction validator.</summary>
    Redaction,

    /// <summary>Step 4: the attachment hash, type, header and dimensions.</summary>
    Attachment,

    /// <summary>Step 5: the fingerprint, recomputed on the server.</summary>
    Fingerprint,

    /// <summary>Step 6: symbolication. Missing symbols never refuse a report.</summary>
    Symbolication,
}

/// <summary>The stable wire names of <see cref="IngestionStep"/>, for the audit entry.</summary>
public static class IngestionSteps
{
    /// <summary>Returns the stable snake_case name of a step.</summary>
    public static string ToWireName(this IngestionStep step) => step switch
    {
        IngestionStep.InstallOwnership => "install_ownership",
        IngestionStep.SchemaAndSize => "schema_and_size",
        IngestionStep.Redaction => "redaction",
        IngestionStep.Attachment => "attachment",
        IngestionStep.Fingerprint => "fingerprint",
        IngestionStep.Symbolication => "symbolication",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown ingestion step."),
    };
}
