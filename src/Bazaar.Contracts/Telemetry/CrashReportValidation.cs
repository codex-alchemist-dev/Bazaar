using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Contracts.Telemetry;

/// <summary>Size limits for crash reports and their attachments (doc 43, section 5.2).</summary>
public static class CrashReportLimits
{
    /// <summary>Report JSON before transport: 256 KiB.</summary>
    public const int MaxReportBytes = 256 * 1024;

    /// <summary>The <c>logs</c> array after redaction: 64 KiB.</summary>
    public const int MaxLogsBytes = 64 * 1024;

    /// <summary>Largest screenshot chunk on <c>crash.attachment.chunk</c>. Chosen here; doc 43 does not set one.</summary>
    public const int MaxAttachmentChunkBytes = 64 * 1024;

    /// <summary>Length of a lower-case hex SHA-256.</summary>
    public const int Sha256HexLength = 64;
}

/// <summary>
/// Checks crash reports and attachment messages before they are sent or accepted. Each method returns one message per
/// problem and never throws; an empty result means the input is valid.
/// </summary>
public static partial class CrashReportValidation
{
    private const string _sha256Pattern = "^[0-9a-f]{64}\\z";

    /// <summary>Checks a report against the schema, size limits, error code shape and flagged frame index.</summary>
    public static IReadOnlyList<string> Validate(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var problems = new List<string>();

        if (!string.Equals(report.Schema, CrashReportSchemas.V1, StringComparison.Ordinal))
        {
            problems.Add($"Unsupported schema '{report.Schema}'; expected '{CrashReportSchemas.V1}'.");
        }

        if (report.Error.Code is not null && !TelemetryErrorCodes.IsWellFormed(report.Error.Code))
        {
            problems.Add($"Malformed error code '{report.Error.Code}'; expected BZ-<AREA>-<NNNN>.");
        }

        if (report.FlaggedFrame is { } flagged)
        {
            if (flagged < 0 || flagged >= report.Stack.Count)
            {
                problems.Add($"Flagged frame {flagged} is outside the stack of {report.Stack.Count} frames.");
            }
        }

        var logsBytes = JsonSerializer.SerializeToUtf8Bytes(
            report.Logs,
            CrashContractsJsonContext.Default.IReadOnlyListCrashLogEntry).Length;
        if (logsBytes > CrashReportLimits.MaxLogsBytes)
        {
            problems.Add($"Log array is {logsBytes} bytes; the limit is {CrashReportLimits.MaxLogsBytes}.");
        }

        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(
            report,
            CrashContractsJsonContext.Default.CrashReport).Length;
        if (reportBytes > CrashReportLimits.MaxReportBytes)
        {
            problems.Add($"Report is {reportBytes} bytes; the limit is {CrashReportLimits.MaxReportBytes}.");
        }

        return problems;
    }

    /// <summary>Checks one screenshot chunk: a non-negative index and 1 to the chunk limit of bytes.</summary>
    public static IReadOnlyList<string> ValidateAttachmentChunk(CrashAttachmentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var problems = new List<string>();

        if (chunk.Index < 0)
        {
            problems.Add($"Chunk index {chunk.Index} is negative.");
        }

        if (chunk.Data.Length == 0)
        {
            problems.Add("Chunk is empty.");
        }
        else if (chunk.Data.Length > CrashReportLimits.MaxAttachmentChunkBytes)
        {
            problems.Add($"Chunk is {chunk.Data.Length} bytes; the limit is {CrashReportLimits.MaxAttachmentChunkBytes}.");
        }

        return problems;
    }

    /// <summary>Checks the closing message: at least one chunk and a lower-case hex SHA-256.</summary>
    public static IReadOnlyList<string> ValidateAttachmentEnd(CrashAttachmentEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);

        var problems = new List<string>();

        if (end.ChunkCount < 1)
        {
            problems.Add($"Attachment has {end.ChunkCount} chunks; it needs at least one.");
        }

        if (!Sha256Regex().IsMatch(end.Sha256))
        {
            problems.Add($"SHA-256 must be {CrashReportLimits.Sha256HexLength} lower-case hex characters.");
        }

        return problems;
    }

    [GeneratedRegex(_sha256Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
