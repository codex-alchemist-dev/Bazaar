using System.Globalization;
using System.Text;

namespace Bazaar.Server.AuditSinks;

/// <summary>How a syslog message is framed on the wire.</summary>
public enum SyslogFraming
{
    /// <summary>One message per datagram (UDP).</summary>
    None,

    /// <summary>RFC 6587 octet counting, <c>LEN SP MSG</c> (TCP).</summary>
    OctetCounting,
}

/// <summary>Settings for <see cref="SyslogSink"/>. Facility 13 is "log audit"; the SD id uses the documentation enterprise number until the operator sets their own.</summary>
public sealed record SyslogOptions(
    string Hostname,
    string AppName = "bazaar",
    int Facility = 13,
    SyslogFraming Framing = SyslogFraming.None,
    string StructuredDataId = "bazaar@32473");

/// <summary>
/// RFC 5424 syslog sink (doc 48 section 5.4). The transport is a port, so UDP, TCP and TLS are the host's choice.
/// Structured data carries the sequence, actor, hash and idempotency key (syslog has no ack, so a receiver dedupes on
/// <c>idem</c>); the message body is the stored payload, so personal fields stay ciphertext.
/// </summary>
public sealed class SyslogSink : IAuditSink
{
    private const int _infoSeverity = 6;
    private readonly SyslogOptions _options;
    private readonly ISyslogTransport _transport;

    public SyslogSink(SyslogOptions options, ISyslogTransport transport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        if (options.Facility is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Syslog facility must be 0 to 23.");
        }

        _options = options;
        _transport = transport;
    }

    public string Name => "syslog";

    public async Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            var sd = new[]
            {
                ("seq", row.Seq.ToString(CultureInfo.InvariantCulture)),
                ("actor", row.Actor),
                ("hash", Convert.ToHexStringLower(row.Hash)),
                ("idem", row.IdempotencyKey),
            };
            await SendAsync(row.Ts, MsgId(row.Kind), sd, row.Payload, ct).ConfigureAwait(false);
        }
    }

    public async Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        foreach (var cp in checkpoints)
        {
            var sd = new[]
            {
                ("first", cp.FirstSeq.ToString(CultureInfo.InvariantCulture)),
                ("last", cp.LastSeq.ToString(CultureInfo.InvariantCulture)),
                ("head", Convert.ToHexStringLower(cp.ChainHeadHash)),
                ("keyId", cp.KeyId),
                ("sig", Convert.ToBase64String(cp.Signature)),
                ("idem", cp.IdempotencyKey),
            };
            await SendAsync(cp.CreatedAt, "audit.checkpoint", sd, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds one RFC 5424 message: <c>&lt;PRI&gt;1 TIMESTAMP HOST APP PROCID MSGID [SD] MSG</c>.</summary>
    internal string Format(DateTimeOffset ts, string msgId, IEnumerable<(string Name, string Value)> sd, string? body)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append((_options.Facility * 8) + _infoSeverity).Append(">1 ")
            .Append(ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)).Append(' ')
            .Append(Token(_options.Hostname, 255)).Append(' ')
            .Append(Token(_options.AppName, 48)).Append(" - ")
            .Append(msgId).Append(" [").Append(Token(_options.StructuredDataId, 32));
        foreach (var (name, value) in sd)
        {
            sb.Append(' ').Append(name).Append("=\"").Append(EscapeParam(value)).Append('"');
        }

        sb.Append(']');
        if (!string.IsNullOrEmpty(body))
        {
            sb.Append(' ').Append(body);
        }

        return sb.ToString();
    }

    private async Task SendAsync(DateTimeOffset ts, string msgId, IEnumerable<(string, string)> sd, string? body, CancellationToken ct)
    {
        var message = Encoding.UTF8.GetBytes(Format(ts, msgId, sd, body));
        if (_options.Framing == SyslogFraming.OctetCounting)
        {
            message = Encoding.UTF8.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " ").Concat(message).ToArray();
        }

        await _transport.SendAsync(message, ct).ConfigureAwait(false);
    }

    // PRINTUSASCII 33..126 only, per RFC 5424 section 6; anything else becomes '_'; empty is the nil value.
    private static string Token(string value, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (sb.Length == max)
            {
                break;
            }

            sb.Append(c is >= '!' and <= '~' ? c : '_');
        }

        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static string MsgId(string kind) => Token(kind, 32);

    private static string EscapeParam(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
