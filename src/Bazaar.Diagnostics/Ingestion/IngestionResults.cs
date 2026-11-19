using Bazaar.Contracts.Telemetry;

namespace Bazaar.Diagnostics.Ingestion;

/// <summary>The verdict of one check step: a pass, or a refusal with its code and a short detail for the audit log.</summary>
/// <param name="Refusal">The refusal code, or null when the step passed.</param>
/// <param name="Detail">A short, plain description for the audit log. Never shown to the client.</param>
public readonly record struct StepVerdict(IngestionRefusalCode? Refusal, string? Detail)
{
    /// <summary>The step passed.</summary>
    public static StepVerdict Pass => default;

    /// <summary>The step refused the report.</summary>
    public static StepVerdict Refuse(IngestionRefusalCode code, string detail) => new(code, detail);
}

/// <summary>The server's recomputed fingerprint and whether the device's hint differed (doc 43, section 8.3).</summary>
/// <param name="Value">The server value, in <c>fp1:</c> form. This is the value the server uses.</param>
/// <param name="HintMismatch">True when the report carried a hint that differs from <paramref name="Value"/>.</param>
public sealed record FingerprintResult(string Value, bool HintMismatch);

/// <summary>What happened to an attachment (doc 43, section 5.2): kept, or dropped with the code that says why.</summary>
/// <param name="DropReason">The code for a dropped attachment, or null when it is kept.</param>
/// <param name="Detail">A short detail for the audit log, or null when kept.</param>
public sealed record AttachmentDecision(IngestionRefusalCode? DropReason, string? Detail)
{
    /// <summary>True when the attachment passed every check and is kept.</summary>
    public bool Kept => DropReason is null;

    /// <summary>The attachment passed.</summary>
    public static AttachmentDecision Keep => new(null, null);

    /// <summary>The attachment is dropped. The report is still kept.</summary>
    public static AttachmentDecision Drop(IngestionRefusalCode code, string detail) => new(code, detail);
}

/// <summary>A screenshot as the server has assembled it: its begin and end messages and the bytes of all chunks in order.</summary>
/// <param name="Begin">The <c>crash.attachment.begin</c> message.</param>
/// <param name="End">The <c>crash.attachment.end</c> message with the declared SHA-256.</param>
/// <param name="Data">The assembled bytes.</param>
public sealed record IngestionAttachment(CrashAttachmentBegin Begin, CrashAttachmentEnd End, ReadOnlyMemory<byte> Data);

/// <summary>One report as the server received it, with its attachment when the attachment has finished uploading.</summary>
/// <param name="Report">The report.</param>
/// <param name="Attachment">The attachment, or null when none was sent with this report.</param>
public sealed record IngestionSubmission(CrashReport Report, IngestionAttachment? Attachment = null);

/// <summary>The result of <see cref="CrashIngestionPipeline.Process"/>.</summary>
public abstract record IngestionOutcome;

/// <summary>The report was refused at <paramref name="Step"/>. Nothing after that step ran.</summary>
/// <param name="Step">The step that refused the report.</param>
/// <param name="Code">The refusal code. This is the code a client shows.</param>
/// <param name="Detail">A short detail for the audit log.</param>
public sealed record IngestionRefused(IngestionStep Step, IngestionRefusalCode Code, string Detail) : IngestionOutcome;

/// <summary>The report passed every step. Storage and filing (steps 7 and 8, doc 43 section 12.1) follow outside this pipeline.</summary>
/// <param name="Fingerprint">The server's fingerprint, which is the value used for grouping.</param>
/// <param name="FingerprintHintMismatch">True when the device's hint differed. Counted as a normalisation signal (section 8.3).</param>
/// <param name="SymbolsState">Whether the flagged frame was symbolicated.</param>
/// <param name="Attachment">The attachment decision, or null when no attachment was part of this submission.</param>
public sealed record IngestionAccepted(
    string Fingerprint,
    bool FingerprintHintMismatch,
    SymbolsState SymbolsState,
    AttachmentDecision? Attachment) : IngestionOutcome;
