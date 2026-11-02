using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Skimmer.Digest;

/// <summary>
/// Turns one Skimmer gate decision into the inbox item it needs, or into nothing (doc 13, "Digest").
/// The planner is pure: it reads no clock and does no I/O. The inbox writer (doc 09) persists the result.
/// </summary>
public static class DigestPlanner
{
    private const string _wake = "wake";
    private const string _defer = "defer";
    private const string _quarantine = "quarantine";
    private const string _scored = "scored";

    private static readonly FrozenSet<string> _knownPaths = new[]
    {
        "mention", "dm", "critical", _quarantine, _scored, "default",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Plans the inbox item for one decision. A defer enqueues <see cref="InboxKind.GcDigest"/> due at
    /// <paramref name="digestAt"/>. A scored wake enqueues <see cref="InboxKind.SkimmerPromoted"/> due at
    /// <paramref name="now"/>. Mention, DM and critical wakes take the normal wake path, so they return null.
    /// A digest time in the past is accepted as given.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>decision.Seq</c> is below 1.</exception>
    /// <exception cref="ArgumentException">The decision or path is not a known value.</exception>
    /// <exception cref="NotSupportedException"><paramref name="quoteMode"/> is <see cref="DigestQuoteMode.EncryptedExcerpt"/>.</exception>
    public static DigestEnqueue? Plan(GateDecision decision, DateTimeOffset now, DateTimeOffset digestAt, DigestQuoteMode quoteMode)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentOutOfRangeException.ThrowIfLessThan(decision.Seq, 1L);

        if (quoteMode == DigestQuoteMode.EncryptedExcerpt)
        {
            throw new NotSupportedException("encrypted_excerpt needs the per-person key (doc 14); not implemented");
        }

        if (!_knownPaths.Contains(decision.Path))
        {
            throw new ArgumentException($"Unknown gate path '{decision.Path}'.", nameof(decision));
        }

        switch (decision.Decision)
        {
            case _defer:
                return Build(decision, InboxKind.GcDigest, digestAt, quarantined: decision.Path == _quarantine);

            case _wake when decision.Path == _scored:
                return Build(decision, InboxKind.SkimmerPromoted, now, quarantined: false);

            case _wake:
                return null;

            default:
                throw new ArgumentException($"Unknown gate decision '{decision.Decision}'.", nameof(decision));
        }
    }

    private static DigestEnqueue Build(GateDecision decision, string kind, DateTimeOffset dueAt, bool quarantined) =>
        new(
            decision.AgentId,
            kind,
            decision.GcId,
            decision.Seq,
            dueAt,
            BuildPayload(decision, quarantined),
            DigestEnqueue.ForKey(decision.AgentId, decision.GcId, decision.Seq, kind));

    /// <summary>
    /// Writes the payload keys <c>gc_id</c>, <c>seq</c>, <c>category</c> and <c>reason</c>. The key
    /// <c>quarantined</c> is added only when true. No message text is ever written.
    /// </summary>
    private static string BuildPayload(GateDecision decision, bool quarantined)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("gc_id", decision.GcId);
            writer.WriteNumber("seq", decision.Seq);
            writer.WriteString("category", decision.Category);
            writer.WriteString("reason", decision.Path);
            if (quarantined)
            {
                writer.WriteBoolean("quarantined", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
