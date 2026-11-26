using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics;

/// <summary>Why a screenshot upload message was refused. The wire names are stable (doc 43 section 5.2); add a code, never rename one.</summary>
public enum AttachmentUploadRefusal
{
    /// <summary>The kind is not <c>screenshot</c>, or the declared size is below one byte.</summary>
    InvalidBegin,

    /// <summary>The declared content type is not PNG or JPEG.</summary>
    UnsupportedType,

    /// <summary>The declared size is over the attachment cap.</summary>
    TooLarge,

    /// <summary>An upload with this attachment id is already open on this connection.</summary>
    DuplicateSession,

    /// <summary>This connection already has its maximum number of open uploads, or the server has too many.</summary>
    SessionLimit,

    /// <summary>No open upload has this attachment id on this connection. It was never begun, or it expired.</summary>
    UnknownSession,

    /// <summary>The chunk index is not the next one expected.</summary>
    OutOfOrder,

    /// <summary>A chunk carries no bytes.</summary>
    EmptyChunk,

    /// <summary>A chunk is over the chunk cap.</summary>
    ChunkTooLarge,

    /// <summary>The upload has reached the chunk-count cap.</summary>
    TooManyChunks,

    /// <summary>The chunks would exceed the size declared in <c>crash.attachment.begin</c>.</summary>
    OverDeclaredSize,

    /// <summary>The server's memory budget for open uploads is used up.</summary>
    MemoryLimit,

    /// <summary>The end message names a different number of chunks than were received.</summary>
    ChunkCountMismatch,
}

/// <summary>The stable wire names of <see cref="AttachmentUploadRefusal"/>.</summary>
public static class AttachmentUploadRefusals
{
    /// <summary>The snake_case name a client sees in an error.</summary>
    public static string ToWireName(this AttachmentUploadRefusal refusal) => refusal switch
    {
        AttachmentUploadRefusal.InvalidBegin => "attachment_invalid_begin",
        AttachmentUploadRefusal.UnsupportedType => "attachment_type_not_allowed",
        AttachmentUploadRefusal.TooLarge => "attachment_too_large",
        AttachmentUploadRefusal.DuplicateSession => "attachment_duplicate",
        AttachmentUploadRefusal.SessionLimit => "attachment_session_limit",
        AttachmentUploadRefusal.UnknownSession => "attachment_unknown",
        AttachmentUploadRefusal.OutOfOrder => "attachment_chunk_out_of_order",
        AttachmentUploadRefusal.EmptyChunk => "attachment_chunk_empty",
        AttachmentUploadRefusal.ChunkTooLarge => "attachment_chunk_too_large",
        AttachmentUploadRefusal.TooManyChunks => "attachment_too_many_chunks",
        AttachmentUploadRefusal.OverDeclaredSize => "attachment_size_mismatch",
        AttachmentUploadRefusal.MemoryLimit => "attachment_memory_limit",
        AttachmentUploadRefusal.ChunkCountMismatch => "attachment_chunk_count_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown upload refusal."),
    };
}

/// <summary>
/// Limits for screenshot uploads (doc 43 sections 5.2 and 11.4). The byte cap and the chunk cap come from the contracts and
/// <see cref="IngestionLimits"/>; the rest are chosen here and can be set by the operator.
/// </summary>
public sealed record AttachmentUploadOptions
{
    /// <summary>Open uploads one connection may hold at once. Chosen here.</summary>
    public int MaxSessionsPerConnection { get; init; } = 2;

    /// <summary>Open uploads the server holds across all connections. Chosen here.</summary>
    public int MaxOpenSessions { get; init; } = 256;

    /// <summary>Bytes held by open uploads across all connections. Chosen here.</summary>
    public long MaxOpenBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Chunks one upload may send. Chosen here: 4096 chunks of at least one byte cover any upload under the byte cap.</summary>
    public int MaxChunkCount { get; init; } = 4096;

    /// <summary>How long an upload may stay open after its begin message. Chosen here.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Assembles <c>crash.attachment.begin</c>, <c>.chunk</c> and <c>.end</c> into one <see cref="IngestionAttachment"/> (doc 43 section
/// 5.2). It keeps upload sessions in memory, keyed by connection and attachment id. Every byte is treated as hostile: each
/// message is checked before any memory is taken, the bytes held never exceed the declared size or the server's budget, and an
/// upload that is never finished expires. It does no I/O and never hashes or decodes the image; the ingestion pipeline does that.
/// </summary>
public sealed class AttachmentUploadAssembler
{
    private readonly AttachmentUploadOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Connection, Guid Attachment), Session> _sessions = [];
    private readonly Dictionary<Guid, int> _perConnection = [];
    private long _openBytes;

    /// <summary>Creates the assembler.</summary>
    public AttachmentUploadAssembler(AttachmentUploadOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.SessionLifetime <= TimeSpan.Zero || options.MaxOpenSessions < 1 || options.MaxSessionsPerConnection < 1
            || options.MaxChunkCount < 1 || options.MaxOpenBytes < 1)
        {
            throw new ArgumentException("Upload limits must be positive.", nameof(options));
        }

        _options = options;
        _clock = clock;
    }

    /// <summary>The bytes held by open uploads right now.</summary>
    public long OpenBytes
    {
        get
        {
            lock (_gate)
            {
                return _openBytes;
            }
        }
    }

    /// <summary>Opens an upload. Returns the refusal, or null when the upload is open.</summary>
    public AttachmentUploadRefusal? Begin(Guid connectionId, CrashAttachmentBegin begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        if (!string.Equals(begin.Kind, "screenshot", StringComparison.Ordinal) || begin.TotalBytes < 1)
        {
            return AttachmentUploadRefusal.InvalidBegin;
        }

        if (begin.TotalBytes > IngestionLimits.MaxAttachmentBytes)
        {
            return AttachmentUploadRefusal.TooLarge;
        }

        if (ImageHeaders.FormatOf(begin.ContentType) is null)
        {
            return AttachmentUploadRefusal.UnsupportedType;
        }

        lock (_gate)
        {
            Sweep(_clock.GetUtcNow());
            var key = (connectionId, begin.AttachmentId);
            if (_sessions.ContainsKey(key))
            {
                return AttachmentUploadRefusal.DuplicateSession;
            }

            _perConnection.TryGetValue(connectionId, out var mine);
            if (mine >= _options.MaxSessionsPerConnection || _sessions.Count >= _options.MaxOpenSessions)
            {
                return AttachmentUploadRefusal.SessionLimit;
            }

            _sessions[key] = new Session(begin, _clock.GetUtcNow() + _options.SessionLifetime);
            _perConnection[connectionId] = mine + 1;
            return null;
        }
    }

    /// <summary>Adds one chunk to an open upload. Returns the refusal, or null when the chunk was kept.</summary>
    public AttachmentUploadRefusal? Chunk(Guid connectionId, CrashAttachmentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_gate)
        {
            Sweep(_clock.GetUtcNow());
            if (!_sessions.TryGetValue((connectionId, chunk.AttachmentId), out var session))
            {
                return AttachmentUploadRefusal.UnknownSession;
            }

            if (chunk.Index != session.Chunks.Count)
            {
                return AttachmentUploadRefusal.OutOfOrder;
            }

            if (chunk.Data is null || chunk.Data.Length == 0)
            {
                return AttachmentUploadRefusal.EmptyChunk;
            }

            if (chunk.Data.Length > CrashReportLimits.MaxAttachmentChunkBytes)
            {
                return AttachmentUploadRefusal.ChunkTooLarge;
            }

            if (session.Chunks.Count >= _options.MaxChunkCount)
            {
                return AttachmentUploadRefusal.TooManyChunks;
            }

            if (session.Bytes + chunk.Data.Length > session.Begin.TotalBytes)
            {
                return AttachmentUploadRefusal.OverDeclaredSize;
            }

            if (_openBytes + chunk.Data.Length > _options.MaxOpenBytes)
            {
                return AttachmentUploadRefusal.MemoryLimit;
            }

            session.Chunks.Add(chunk.Data);
            session.Bytes += chunk.Data.Length;
            _openBytes += chunk.Data.Length;
            return null;
        }
    }

    /// <summary>
    /// Closes an open upload and returns it assembled. The upload is closed whether or not it succeeds. The SHA-256 is not checked
    /// here; the ingestion pipeline checks it, together with the size and the image header.
    /// </summary>
    public (AttachmentUploadRefusal? Refusal, IngestionAttachment? Attachment) End(Guid connectionId, CrashAttachmentEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        lock (_gate)
        {
            Sweep(_clock.GetUtcNow());
            if (!_sessions.TryGetValue((connectionId, end.AttachmentId), out var session))
            {
                return (AttachmentUploadRefusal.UnknownSession, null);
            }

            Close(connectionId, end.AttachmentId, session);
            if (session.Chunks.Count != end.ChunkCount)
            {
                return (AttachmentUploadRefusal.ChunkCountMismatch, null);
            }

            var data = new byte[session.Bytes];
            var offset = 0;
            foreach (var chunk in session.Chunks)
            {
                chunk.CopyTo(data, offset);
                offset += chunk.Length;
            }

            return (null, new IngestionAttachment(session.Begin, end, data));
        }
    }

    /// <summary>Drops every open upload of a connection, for example when the connection closes.</summary>
    public void Forget(Guid connectionId)
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Keys.Where(k => k.Connection == connectionId).ToList())
            {
                Close(key.Connection, key.Attachment, _sessions[key]);
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, session) in _sessions.Where(s => s.Value.ExpiresAt <= now).ToList())
        {
            Close(key.Connection, key.Attachment, session);
        }
    }

    private void Close(Guid connectionId, Guid attachmentId, Session session)
    {
        _sessions.Remove((connectionId, attachmentId));
        _openBytes -= session.Bytes;
        if (_perConnection.TryGetValue(connectionId, out var count))
        {
            if (count <= 1)
            {
                _perConnection.Remove(connectionId);
            }
            else
            {
                _perConnection[connectionId] = count - 1;
            }
        }
    }

    private sealed class Session(CrashAttachmentBegin begin, DateTimeOffset expiresAt)
    {
        public CrashAttachmentBegin Begin { get; } = begin;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public List<byte[]> Chunks { get; } = [];

        public long Bytes { get; set; }
    }
}
