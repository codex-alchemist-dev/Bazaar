using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Portability.Verifier;

/// <summary>One thing that is wrong with a bundle. <see cref="Code"/> is a stable id, <see cref="Detail"/> a fixed description.</summary>
public sealed record VerifyProblem(string Code, string? Path, string Detail);

/// <summary>One named group of checks. Status is passed, failed or skipped.</summary>
public sealed record VerifyCheck(string Name, string Status);

/// <summary>Totals from the audit replay.</summary>
public sealed record AuditSummary(long Rows, long Checkpoints, long UncheckpointedTailRows, long ProofsChecked);

/// <summary>Totals from the GC chain checks.</summary>
public sealed record GcSummary(int Gcs, long Events);

/// <summary>
/// The verification result. It carries no clock value and no host path, and every list is sorted, so the same bundle always
/// yields the same bytes. <see cref="NotChecked"/> names what this verifier does not check, so a pass is never read as more
/// than it is.
/// </summary>
public sealed record VerifyReport(
    string Format,
    bool Verified,
    string? SchemaVersion,
    string? InstanceId,
    string SigningKeySource,
    long FilesListed,
    long FilesHashed,
    long BytesHashed,
    IReadOnlyList<string> UnlistedFiles,
    IReadOnlyList<VerifyCheck> Checks,
    IReadOnlyList<VerifyProblem> Problems,
    AuditSummary? Audit,
    GcSummary? Gc,
    IReadOnlyList<string> NotChecked)
{
    /// <summary>Format id of the report.</summary>
    public const string FormatId = "bazaar.verify.report.v1";

    /// <summary>The deterministic JSON text: indented, snake_case, LF line ends, with a trailing newline.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, VerifyJson.Default.VerifyReport).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VerifyReport))]
internal sealed partial class VerifyJson : JsonSerializerContext;
