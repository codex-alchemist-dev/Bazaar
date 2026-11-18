namespace Bazaar.Contracts.Telemetry;

/// <summary>Message types (<c>t</c>) for crash reports (doc 43, sections 5.2 and 16 item 4).</summary>
public static class CrashWireTypes
{
    /// <summary>Client sends one crash report. Payload: <see cref="CrashSubmit"/>. Answered with <see cref="CrashAck"/>.</summary>
    public const string Submit = "crash.submit";

    /// <summary>Server acknowledges a report. Payload: <see cref="CrashAck"/>.</summary>
    public const string Ack = "crash.ack";

    /// <summary>Client opens a screenshot upload. Payload: <see cref="CrashAttachmentBegin"/>.</summary>
    public const string AttachmentBegin = "crash.attachment.begin";

    /// <summary>Client sends one piece of an open screenshot upload. Payload: <see cref="CrashAttachmentChunk"/>.</summary>
    public const string AttachmentChunk = "crash.attachment.chunk";

    /// <summary>Client closes a screenshot upload with its SHA-256. Payload: <see cref="CrashAttachmentEnd"/>.</summary>
    public const string AttachmentEnd = "crash.attachment.end";
}

/// <summary>Payload of <c>crash.submit</c>: one report. The envelope <c>id</c> is the request id.</summary>
public sealed record CrashSubmit(CrashReport Report);

/// <summary>
/// Payload of <c>crash.ack</c>. <see cref="ReportId"/> echoes the device's report id; the server keys reports on
/// (install id, report id), so a resend is answered with the same ack. <see cref="GroupKey"/> is the issue group the server filed the report under.
/// </summary>
public sealed record CrashAck(Guid ReportId, string GroupKey);

/// <summary>
/// Payload of <c>crash.attachment.begin</c>. <see cref="ContentType"/> is a media type such as <c>image/png</c>.
/// <see cref="TotalBytes"/> is the size of the whole attachment. The report refers to <see cref="AttachmentId"/> and may arrive first.
/// </summary>
public sealed record CrashAttachmentBegin(
    Guid AttachmentId,
    Guid ReportId,
    string Kind,
    string ContentType,
    long TotalBytes);

/// <summary>Payload of <c>crash.attachment.chunk</c>. <see cref="Index"/> counts from 0 and gives the order of <see cref="Data"/>.</summary>
public sealed record CrashAttachmentChunk(Guid AttachmentId, int Index, byte[] Data);

/// <summary>
/// Payload of <c>crash.attachment.end</c>. <see cref="Sha256"/> is the lower-case hex SHA-256 of the whole attachment.
/// The server keeps the attachment only if the hash matches.
/// </summary>
public sealed record CrashAttachmentEnd(Guid AttachmentId, int ChunkCount, string Sha256);
