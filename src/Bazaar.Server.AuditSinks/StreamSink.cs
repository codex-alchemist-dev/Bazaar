using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.AuditSinks;

/// <summary>Settings for <see cref="StreamSink"/>.</summary>
public sealed record StreamSinkOptions(string ServiceName = "bazaar");

/// <summary>
/// OpenTelemetry-style stream sink (doc 48 section 5.4): one JSON log record per line, using the OTel logs data model
/// field names (<c>timestamp</c>, <c>severity_text</c>, <c>body</c>, <c>resource</c>, <c>attributes</c>). No OTel package is
/// used; the line writer is a port. The body is the stored payload, so personal fields stay ciphertext.
/// </summary>
public sealed class StreamSink : IAuditSink
{
    private readonly StreamSinkOptions _options;
    private readonly IAuditStreamWriter _writer;

    public StreamSink(StreamSinkOptions options, IAuditStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        _options = options;
        _writer = writer;
    }

    public string Name => "otel-stream";

    public async Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            var line = Line(row.Ts, row.Payload, w =>
            {
                w.WriteNumber("bazaar.audit.seq", row.Seq);
                w.WriteString("bazaar.audit.kind", row.Kind);
                w.WriteString("bazaar.audit.actor", row.Actor);
                w.WriteString("bazaar.audit.hash", Convert.ToHexStringLower(row.Hash));
                w.WriteString("bazaar.audit.idempotency_key", row.IdempotencyKey);
            });
            await _writer.WriteLineAsync(line, ct).ConfigureAwait(false);
        }
    }

    public async Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        foreach (var cp in checkpoints)
        {
            var line = Line(cp.CreatedAt, string.Empty, w =>
            {
                w.WriteString("bazaar.audit.kind", "audit.checkpoint");
                w.WriteNumber("bazaar.audit.first_seq", cp.FirstSeq);
                w.WriteNumber("bazaar.audit.last_seq", cp.LastSeq);
                w.WriteString("bazaar.audit.head", Convert.ToHexStringLower(cp.ChainHeadHash));
                w.WriteString("bazaar.audit.key_id", cp.KeyId);
                w.WriteString("bazaar.audit.signature", Convert.ToBase64String(cp.Signature));
                w.WriteString("bazaar.audit.idempotency_key", cp.IdempotencyKey);
            });
            await _writer.WriteLineAsync(line, ct).ConfigureAwait(false);
        }
    }

    private string Line(DateTimeOffset ts, string body, Action<Utf8JsonWriter> attributes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("timestamp", ts);
            w.WriteString("severity_text", "INFO");
            w.WriteString("body", body);
            w.WriteStartObject("resource");
            w.WriteString("service.name", _options.ServiceName);
            w.WriteEndObject();
            w.WriteStartObject("attributes");
            attributes(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
