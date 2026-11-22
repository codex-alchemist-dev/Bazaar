using System.Text;
using System.Text.Json.Nodes;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Audit;

/// <summary>
/// Writes display audit rows to the audit log (docs/design/59, section 5, rules 2 and 6; docs/design/15, "Audit log and kill
/// switch"). A row carries ids, source, sizes and the frame's SHA-256, never pixels, typed text, the backend reference or the
/// browser session id. <see cref="IDisplayAuditSink"/> is synchronous, so each write waits for the log, bounded by
/// <see cref="DisplayAuditOptions.WriteTimeoutSeconds"/>. A failed or timed-out write throws <see cref="DisplayAuditException"/>:
/// a capture whose row was not written is not returned, and a missing log refuses the same way.
/// </summary>
/// <remarks>
/// The kinds and the actor form <c>agent:{id}</c> are this module's choice. docs/design/15 names no display audit kinds.
/// </remarks>
public sealed class AuditLogDisplayAuditSink : IDisplayAuditSink
{
    /// <summary>Kind of a row for a frame returned to a caller.</summary>
    public const string CapturedKind = "vm-display.captured";

    /// <summary>Kind of a row for input that was dispatched.</summary>
    public const string InputSentKind = "vm-display.input_sent";

    /// <summary>Kind of a row for a refused or rejected call.</summary>
    public const string RefusedKind = "vm-display.refused";

    private readonly IAuditLog? _log;
    private readonly TimeProvider _time;
    private readonly DisplayAuditOptions _options;

    /// <summary>Creates the sink. A <see langword="null"/> log makes every write fail closed.</summary>
    public AuditLogDisplayAuditSink(IAuditLog? log, TimeProvider time, DisplayAuditOptions options)
    {
        _log = log;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonObject payload = Base(target);
        payload["source"] = source.ToString();
        payload["width"] = width;
        payload["height"] = height;
        payload["bytes"] = bytes;
        payload["sha256"] = HexOnly(sha256);
        Write(CapturedKind, target, payload);
    }

    /// <inheritdoc />
    public void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonObject payload = Base(target);
        payload["source"] = source.ToString();
        payload["action_count"] = actionCount;
        Write(InputSentKind, target, payload);
    }

    /// <inheritdoc />
    public void Refused(DisplayTarget target, string operation, string reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonObject payload = Base(target);
        payload["operation"] = Clean(operation);
        payload["reason"] = Clean(reason);
        Write(RefusedKind, target, payload);
    }

    private static JsonObject Base(DisplayTarget target) => new()
    {
        ["agent_id"] = target.AgentId,
        ["vm_id"] = target.VmId.ToString("D"),
        ["os"] = target.Os.ToString(),
    };

    /// <summary>Keeps only lower-case hex digits, so a bad hash value cannot carry text into a row.</summary>
    private static string HexOnly(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Removes control characters and cuts the text to the configured length.</summary>
    private string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, _options.MaxReasonLength));
        foreach (char c in value)
        {
            if (sb.Length >= _options.MaxReasonLength)
                break;
            if (!char.IsControl(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private void Write(string kind, DisplayTarget target, JsonObject payload)
    {
        if (_log is null)
            throw new DisplayAuditException("No audit log is registered, so the display call is not allowed to complete.");

        try
        {
            AuditPayloadGuard.Ensure(payload);
            var entry = new AuditEntry(_time.GetUtcNow(), $"agent:{target.AgentId}", kind, payload.ToJsonString());
            _log.AppendAsync(entry, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(_options.WriteTimeoutSeconds), _time)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception e)
        {
            // The inner exception is not kept: a log provider's message could echo the payload.
            throw new DisplayAuditException($"The display audit row ({kind}) could not be written: {e.GetType().Name}.");
        }
    }
}
