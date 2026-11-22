using System.Buffers;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.WebAdmin.Security;

/// <summary>
/// Writes admin actions to the audit log (doc 51 section 5). Refuses fields that look like secrets, and lets an append failure
/// propagate so the caller refuses the write (fail closed).
/// </summary>
public sealed class AdminAuditRecorder : IAdminAudit
{
    /// <summary>Longest field value accepted; anything longer is more likely a credential or blob than a label.</summary>
    public const int MaxValueLength = 512;

    private static readonly string[] _secretKeyParts = ["token", "secret", "cookie", "password"];

    private readonly IAuditLog _log;
    private readonly TimeProvider _time;

    public AdminAuditRecorder(IAuditLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _log = log;
        _time = time;
    }

    public Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(actor);
        ArgumentNullException.ThrowIfNull(fields);

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (key, value) in fields)
            {
                if (LooksSecret(key, value))
                {
                    throw new ArgumentException("Audit field looks like a secret and was refused.", nameof(fields));
                }

                w.WriteString(key, value);
            }

            w.WriteEndObject();
        }

        var entry = new AuditEntry(_time.GetUtcNow(), actor, kind, Encoding.UTF8.GetString(buffer.WrittenSpan));
        return _log.AppendAsync(entry, ct);
    }

    private static bool LooksSecret(string key, string? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        foreach (var part in _secretKeyParts)
        {
            if (key.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return value is not null && value.Length > MaxValueLength;
    }
}
