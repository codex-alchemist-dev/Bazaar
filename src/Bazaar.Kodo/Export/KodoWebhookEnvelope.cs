using System.Text.Json;

namespace Bazaar.Kodo.Export;

/// <summary>
/// The webhook event types of doc 46 section 15.3. They keep the <c>kodo.</c> prefix, unlike the stream's event types (section 15.6).
/// </summary>
public static class KodoWebhookEvents
{
    /// <summary>The displayed state changed.</summary>
    public const string StateChanged = "kodo.state_changed";

    /// <summary>An alert was raised.</summary>
    public const string Alert = "kodo.alert";

    /// <summary>An incident was opened.</summary>
    public const string IncidentOpened = "kodo.incident.opened";

    /// <summary>An incident was updated.</summary>
    public const string IncidentUpdated = "kodo.incident.updated";

    /// <summary>An incident was closed.</summary>
    public const string IncidentClosed = "kodo.incident.closed";

    /// <summary>A runbook finished.</summary>
    public const string RunbookFinished = "kodo.runbook.finished";

    /// <summary>A health report is ready.</summary>
    public const string ReportReady = "kodo.report.ready";

    /// <summary>Tamper evidence was found.</summary>
    public const string Tamper = "kodo.tamper";

    /// <summary>Every event type a webhook may carry.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        StateChanged,
        Alert,
        IncidentOpened,
        IncidentUpdated,
        IncidentClosed,
        RunbookFinished,
        ReportReady,
        Tamper,
    ];
}

/// <summary>A webhook body ready to sign and send.</summary>
/// <param name="EventType">One of <see cref="KodoWebhookEvents.All"/>.</param>
/// <param name="EventId">The event id, which a consumer uses to deduplicate retries.</param>
/// <param name="OccurredAt">When the event occurred, in UTC.</param>
/// <param name="Body">The UTF-8 JSON body. Sign these exact bytes.</param>
public sealed record KodoWebhookEnvelope(string EventType, string EventId, DateTimeOffset OccurredAt, byte[] Body);

/// <summary>
/// Builds the webhook body (doc 46 section 15.3). Payloads are identifiers only: the consumer calls the read API for detail, so a body
/// never carries free text, content or secrets. Identifier names and values are checked against a small shape.
/// </summary>
public static class KodoWebhookEnvelopeBuilder
{
    /// <summary>The most identifiers one body may carry.</summary>
    public const int MaxIdentifiers = 16;

    /// <summary>Builds an envelope. The body keys are written in a fixed order, so one event has one encoding.</summary>
    /// <param name="eventType">One of <see cref="KodoWebhookEvents.All"/>.</param>
    /// <param name="eventId">The event id: URL-safe characters, up to 128.</param>
    /// <param name="occurredAt">When the event occurred.</param>
    /// <param name="identifiers">The identifiers, such as a probe id. Names are lowercase; values are short identifiers.</param>
    /// <exception cref="ArgumentException">A type, id, name or value is not acceptable.</exception>
    public static KodoWebhookEnvelope Build(
        string eventType,
        string eventId,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string> identifiers)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(identifiers);

        if (!KodoWebhookEvents.All.Contains(eventType, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{eventType}' is not a Kodo webhook event type.", nameof(eventType));
        }

        if (!KodoExportPatterns.EventId().IsMatch(eventId))
        {
            throw new ArgumentException("A webhook event id must be 1 to 128 URL-safe characters.", nameof(eventId));
        }

        if (identifiers.Count > MaxIdentifiers)
        {
            throw new ArgumentException($"A webhook body carries at most {MaxIdentifiers} identifiers.", nameof(identifiers));
        }

        List<KeyValuePair<string, string>> ordered = [];
        foreach (KeyValuePair<string, string> pair in identifiers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!KodoExportPatterns.IdentifierName().IsMatch(pair.Key) || !KodoExportPatterns.IdentifierValue().IsMatch(pair.Value))
            {
                throw new ArgumentException($"'{pair.Key}' is not an identifier the webhook body may carry.", nameof(identifiers));
            }

            ordered.Add(pair);
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", eventType);
            writer.WriteString("id", eventId);
            writer.WriteString("occurredAt", occurredAt.ToUniversalTime());
            writer.WriteStartObject("identifiers");
            foreach (KeyValuePair<string, string> pair in ordered)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new KodoWebhookEnvelope(eventType, eventId, occurredAt, stream.ToArray());
    }
}
