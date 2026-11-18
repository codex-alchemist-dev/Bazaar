using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Diagnostics;

/// <summary>
/// The crash events of doc 43 section 11.5 as audit entries. A payload carries ids, hashes, codes and counts only, never report
/// content, so it needs no redaction and cannot leak.
/// </summary>
internal static class CrashAudit
{
    public const string Actor = "crash-intake";

    public static AuditEntry Accepted(DateTimeOffset now, Guid reportId, string fingerprint, string bodySha256, string symbols, string attachment) =>
        Entry(now, "crash.report.accepted", w =>
        {
            w.WriteString("report_id", reportId);
            w.WriteString("fingerprint", fingerprint);
            w.WriteString("body_sha256", bodySha256);
            w.WriteString("symbols", symbols);
            w.WriteString("attachment", attachment);
        });

    public static AuditEntry Duplicate(DateTimeOffset now, Guid reportId) =>
        Entry(now, "crash.report.duplicate", w => w.WriteString("report_id", reportId));

    public static AuditEntry Refused(DateTimeOffset now, Guid reportId, string step, string code) =>
        Entry(now, "crash.report.refused", w =>
        {
            w.WriteString("report_id", reportId);
            w.WriteString("step", step);
            w.WriteString("code", code);
        });

    public static AuditEntry AttachmentDropped(DateTimeOffset now, Guid reportId, string code) =>
        Entry(now, "crash.attachment.dropped", w =>
        {
            w.WriteString("report_id", reportId);
            w.WriteString("code", code);
        });

    public static AuditEntry FingerprintMismatch(DateTimeOffset now, Guid reportId, string device, string server) =>
        Entry(now, "crash.fingerprint.mismatch", w =>
        {
            w.WriteString("report_id", reportId);
            w.WriteString("device", device);
            w.WriteString("server", server);
        });

    public static AuditEntry GroupStatus(DateTimeOffset now, string fingerprint, string status, long occurrences) =>
        Entry(now, "crash.group.status", w =>
        {
            w.WriteString("fingerprint", fingerprint);
            w.WriteString("status", status);
            w.WriteNumber("occurrences", occurrences);
        });

    public static AuditEntry IssueEvent(DateTimeOffset now, string kind, string fingerprint, int issueNumber) =>
        Entry(now, kind, w =>
        {
            w.WriteString("fingerprint", fingerprint);
            w.WriteNumber("issue", issueNumber);
        });

    public static AuditEntry IssueDeleted(DateTimeOffset now, string fingerprint, int issueNumber) =>
        IssueEvent(now, "crash.issue.deleted_by_hand", fingerprint, issueNumber);

    public static AuditEntry ActionDead(DateTimeOffset now, string fingerprint, string action, int attempts) =>
        Entry(now, "crash.action.dead", w =>
        {
            w.WriteString("fingerprint", fingerprint);
            w.WriteString("action", action);
            w.WriteNumber("attempts", attempts);
        });

    private static AuditEntry Entry(DateTimeOffset now, string kind, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return new AuditEntry(now, Actor, kind, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
