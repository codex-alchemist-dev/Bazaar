using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// Writes egress audit rows (doc 16 WP9). Rule changes always get a row. Allows and denies are limited per agent and kind, so a
/// flood from a guest cannot swamp the log; the rows held back are counted and written as one summary row. Rows carry host
/// and port only, never a path, query or header, which can hold secrets.
/// </summary>
public sealed class EgressAuditor(IAuditLog log, VmEgressOptions options, TimeProvider time)
{
    /// <summary>Audit kind for a rule change.</summary>
    public const string RuleChangeKind = "egress.rules";

    /// <summary>Audit kind for a refused request.</summary>
    public const string DenyKind = "egress.deny";

    /// <summary>Audit kind for an allowed request.</summary>
    public const string AllowKind = "egress.allow";

    /// <summary>Audit kind for the summary of rows held back by the rate limit.</summary>
    public const string SuppressedKind = "egress.suppressed";

    private const string _actor = "vm-egress";

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>Writes a rule-change row. Never rate limited. Exceptions reach the caller, so a change is not silent.</summary>
    public Task RuleChangeAsync(string agentId, string change, IReadOnlyList<string> entries, string? detail, CancellationToken ct = default) =>
        log.AppendAsync(
            new AuditEntry(
                time.GetUtcNow(),
                _actor,
                RuleChangeKind,
                Json(w =>
                {
                    w.WriteString("agent", agentId);
                    w.WriteString("change", change);
                    w.WriteStartArray("entries");
                    foreach (var entry in entries)
                    {
                        w.WriteStringValue(entry);
                    }

                    w.WriteEndArray();
                    if (detail is not null)
                    {
                        w.WriteString("detail", detail);
                    }
                })),
            ct);

    /// <summary>Writes a deny row unless the agent's deny limit for the window is used up. Failures to write never reach the proxy.</summary>
    public Task DenyAsync(string agentId, string host, int port, string reason, CancellationToken ct = default) =>
        RequestAsync(DenyKind, agentId, host, port, reason, ct);

    /// <summary>Writes an allow row, with the same limit as denies but its own count.</summary>
    public Task AllowAsync(string agentId, string host, int port, CancellationToken ct = default) =>
        RequestAsync(AllowKind, agentId, host, port, null, ct);

    /// <summary>
    /// Writes the held-back count for each of the agent's windows as summary rows and clears it. Called when the agent's proxy
    /// stops, so suppressed events are not lost until some later event. Never throws.
    /// </summary>
    public async Task FlushAsync(string agentId)
    {
        foreach (var window in _windows.Values.Where(w => w.Agent == agentId))
        {
            var count = window.TakeSuppressed();
            if (count == 0)
            {
                continue;
            }

            try
            {
                await log.AppendAsync(
                    new AuditEntry(
                        time.GetUtcNow(),
                        _actor,
                        SuppressedKind,
                        Json(w =>
                        {
                            w.WriteString("agent", agentId);
                            w.WriteString("kind", window.Kind);
                            w.WriteNumber("count", count);
                        })),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same rule as the other rows: audit trouble never reaches the proxy.
            }
        }
    }

    private async Task RequestAsync(string kind, string agentId, string host, int port, string? reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var window = _windows.GetOrAdd(kind + "|" + agentId, _ => new Window(agentId, kind, now));
        var (write, suppressed) = window.Take(now, options.AuditWindow, options.AuditRowsPerWindow);
        try
        {
            if (suppressed > 0)
            {
                await log.AppendAsync(
                    new AuditEntry(
                        now,
                        _actor,
                        SuppressedKind,
                        Json(w =>
                        {
                            w.WriteString("agent", agentId);
                            w.WriteString("kind", kind);
                            w.WriteNumber("count", suppressed);
                        })),
                    ct).ConfigureAwait(false);
            }

            if (write)
            {
                await log.AppendAsync(
                    new AuditEntry(
                        now,
                        _actor,
                        kind,
                        Json(w =>
                        {
                            w.WriteString("agent", agentId);
                            w.WriteString("host", host);
                            w.WriteNumber("port", port);
                            if (reason is not null)
                            {
                                w.WriteString("reason", reason);
                            }
                        })),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audit trouble must not turn into a proxy outage or leak into the guest's response.
        }
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Window(string agent, string kind, DateTimeOffset start)
    {
        private readonly object _gate = new();
        private DateTimeOffset _start = start;
        private int _written;
        private int _suppressed;

        public string Agent { get; } = agent;

        public string Kind { get; } = kind;

        /// <summary>Takes and clears the held-back count.</summary>
        public int TakeSuppressed()
        {
            lock (_gate)
            {
                var count = _suppressed;
                _suppressed = 0;
                return count;
            }
        }

        /// <summary>Decides whether this event gets a row. Also returns the count held back in the window that just ended.</summary>
        public (bool Write, int PreviousSuppressed) Take(DateTimeOffset now, TimeSpan length, int limit)
        {
            lock (_gate)
            {
                var previous = 0;
                if (now - _start >= length)
                {
                    previous = _suppressed;
                    _start = now;
                    _written = 0;
                    _suppressed = 0;
                }

                if (_written < limit)
                {
                    _written++;
                    return (true, previous);
                }

                _suppressed++;
                return (false, previous);
            }
        }
    }
}
