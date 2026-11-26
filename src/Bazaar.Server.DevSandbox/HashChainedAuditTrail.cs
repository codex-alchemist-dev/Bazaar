using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// In-memory, append-only, hash-chained audit trail for one sandbox run (doc 48 section 2.1 rule 7). Each event carries the
/// SHA-256 of the previous event; the first event links to 64 zeros. Appends are serialised with a lock, so concurrent writers
/// still produce one valid chain. Read-back is checked with <see cref="VerifyEvents"/>.
/// </summary>
public sealed class HashChainedAuditTrail : ISandboxAuditTrail
{
    /// <summary>The previous-hash value of the first event, and the head hash of an empty trail.</summary>
    private const string _genesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Domain separator so a hash over this encoding cannot be confused with any other hash in the platform.</summary>
    private const string _domainTag = "bazaar.sandbox.audit.v1";

    private readonly object _lock = new();
    private readonly List<SandboxAuditEvent> _events = [];
    private readonly TimeProvider _time;

    /// <summary>Creates an empty trail.</summary>
    /// <param name="time">The clock used for event timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public HashChainedAuditTrail(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<SandboxAuditEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public string HeadHash
    {
        get
        {
            lock (_lock)
            {
                return _events.Count == 0 ? _genesisHash : _events[^1].Hash;
            }
        }
    }

    /// <inheritdoc />
    public SandboxAuditEvent Append(SandboxAction action, SandboxDecision decision, string reason)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(reason);

        lock (_lock)
        {
            var sequence = _events.Count;
            var previousHash = sequence == 0 ? _genesisHash : _events[^1].Hash;
            var at = _time.GetUtcNow();
            var hash = ComputeHash(sequence, at, action, decision, reason, previousHash);
            var appended = new SandboxAuditEvent(sequence, at, action, decision, reason, previousHash, hash);
            _events.Add(appended);
            return appended;
        }
    }

    /// <inheritdoc />
    public bool Verify()
    {
        lock (_lock)
        {
            return VerifyEvents(_events);
        }
    }

    /// <summary>
    /// Checks a list of events read back from storage: sequence numbers run 0, 1, 2 with no gaps, each event links to the
    /// previous event's hash (64 zeros for the first), and each stored hash matches a recomputation over its fields.
    /// Hash comparisons are fixed-time. Any mismatch returns false; an empty list is valid.
    /// </summary>
    /// <param name="events">The events in stored order.</param>
    /// <returns>True when the whole chain verifies.</returns>
    public static bool VerifyEvents(IReadOnlyList<SandboxAuditEvent> events) => VerifyEvents(events, null);

    /// <summary>
    /// Like <see cref="VerifyEvents(IReadOnlyList{SandboxAuditEvent})"/>, and when <paramref name="expectedHeadHash"/> is given (the
    /// head hash stored outside the stream, for example in the run result) the last event's hash must equal it. A chain alone cannot
    /// show that events were cut off its end or that a whole shorter valid chain was swapped in; only the stored head can.
    /// </summary>
    /// <param name="events">The events in stored order.</param>
    /// <param name="expectedHeadHash">The head hash stored with the run result (64 zeros for an empty stream), or null to skip the check.</param>
    /// <returns>True when the chain verifies and, when given, ends at the expected head.</returns>
    public static bool VerifyEvents(IReadOnlyList<SandboxAuditEvent> events, string? expectedHeadHash)
    {
        ArgumentNullException.ThrowIfNull(events);

        var expectedPrevious = _genesisHash;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e is null || e.Action is null || e.Reason is null || e.PreviousHash is null || e.Hash is null)
            {
                return false;
            }

            if (e.Sequence != i)
            {
                return false;
            }

            if (!FixedTimeEquals(e.PreviousHash, expectedPrevious))
            {
                return false;
            }

            var recomputed = ComputeHash(e.Sequence, e.At, e.Action, e.Decision, e.Reason, e.PreviousHash);
            if (!FixedTimeEquals(recomputed, e.Hash))
            {
                return false;
            }

            expectedPrevious = e.Hash;
        }

        return expectedHeadHash is null || FixedTimeEquals(expectedPrevious, expectedHeadHash);
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of one event's canonical encoding. The encoding is: the domain tag, then the sequence
    /// (8 bytes, big-endian), the UTC ticks of <paramref name="at"/> (8 bytes, big-endian), then each string field as a 4-byte
    /// big-endian UTF-8 byte length followed by its bytes. Enum fields are written by name. Every field is length-delimited or
    /// fixed-width, so no two different events share an encoding.
    /// </summary>
    /// <param name="sequence">Zero-based position in the stream.</param>
    /// <param name="at">When the event was recorded.</param>
    /// <param name="action">The action.</param>
    /// <param name="decision">The policy decision.</param>
    /// <param name="reason">The reason for the decision.</param>
    /// <param name="previousHash">The previous event's hash, or 64 zeros for the first event.</param>
    /// <returns>The hash as 64 lowercase hex characters.</returns>
    public static string ComputeHash(
        long sequence,
        DateTimeOffset at,
        SandboxAction action,
        SandboxDecision decision,
        string reason,
        string previousHash)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(previousHash);

        using var canonical = new MemoryStream();
        WriteString(canonical, _domainTag);
        WriteInt64(canonical, sequence);
        WriteInt64(canonical, at.UtcTicks);
        WriteString(canonical, action.Kind.ToString());
        WriteString(canonical, action.Class);
        WriteString(canonical, action.Target);
        WriteString(canonical, action.Payload);
        WriteString(canonical, decision.ToString());
        WriteString(canonical, reason);
        WriteString(canonical, previousHash);

        var digest = SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)));
        return Convert.ToHexStringLower(digest);
    }

    private static void WriteInt64(MemoryStream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(MemoryStream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
