using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// One recorded model call: the request digest, the key it was computed from, and the events the model returned.
/// </summary>
public sealed record CassetteEntry(string Digest, CassetteKey Key, IReadOnlyList<ModelEvent> Events);

/// <summary>
/// Recorded model responses keyed by request digest. Replay is fail-loud: a request with no matching entry throws
/// <see cref="CassetteMismatchException"/> with its digest and the fields that differ from the nearest entry. It never
/// returns a default and never calls a live model. Each entry is checked against its key when it is loaded, so a
/// changed byte in a recorded request throws <see cref="CassetteTamperException"/>.
/// </summary>
/// <remarks>
/// The file format is JSON Lines: one record per call. Recording is explicit only (test.cassette.record_mode); this type
/// adds entries through <see cref="Record"/> and never reaches for a provider itself.
/// </remarks>
public sealed class CassetteStore
{
    private readonly Dictionary<string, CassetteEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<CassetteEntry> _ordered = [];
    private readonly Lock _gate = new();

    /// <summary>Every entry, in the order it was recorded or loaded.</summary>
    public IReadOnlyList<CassetteEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _ordered.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ordered.Count;
            }
        }
    }

    /// <summary>Adds one recorded call. A second entry with the same digest is refused, so an entry cannot be overwritten silently.</summary>
    public CassetteEntry Record(CassetteKey key, IEnumerable<ModelEvent> events)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(events);

        var entry = new CassetteEntry(key.Digest(), key, events.ToArray());
        lock (_gate)
        {
            if (_entries.ContainsKey(entry.Digest))
            {
                throw new InvalidOperationException($"A cassette entry with digest {entry.Digest} is already recorded.");
            }

            _entries.Add(entry.Digest, entry);
            _ordered.Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Returns the recorded events for this request, or throws <see cref="CassetteMismatchException"/> if none matches.
    /// </summary>
    public IReadOnlyList<ModelEvent> Replay(string provider, string model, ModelRequest request)
    {
        var key = CassetteKey.For(provider, model, request);
        var digest = key.Digest();

        CassetteEntry? nearest = null;
        IReadOnlyList<string>? differences = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(digest, out var exact))
            {
                return exact.Events;
            }

            nearest = FindNearest(key, out differences);
        }

        throw new CassetteMismatchException(digest, nearest?.Digest, differences ?? CassetteKey.FieldNames.ToArray(), nearest is null);
    }

    /// <summary>Writes every entry as JSON Lines, one record per line, in recorded order.</summary>
    public string ToJsonLines()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            builder.Append(JsonSerializer.Serialize(ToWire(entry), GatewayJson.Options));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Loads JSON Lines written by <see cref="ToJsonLines"/>. Every record's digest is recomputed from its key; a record
    /// whose key was changed after recording throws <see cref="CassetteTamperException"/> at load, not at replay.
    /// </summary>
    public static CassetteStore FromJsonLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var store = new CassetteStore();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            WireEntry? wire;
            try
            {
                wire = JsonSerializer.Deserialize<WireEntry>(line, GatewayJson.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Cassette line {lineNumber} is not a valid record: {ex.Message}", ex);
            }

            if (wire?.Key is null || wire.Events is null || string.IsNullOrEmpty(wire.Digest))
            {
                throw new InvalidDataException($"Cassette line {lineNumber} is missing its digest, key or events.");
            }

            var recomputed = wire.Key.Digest();
            if (!string.Equals(recomputed, wire.Digest, StringComparison.Ordinal))
            {
                throw new CassetteTamperException(lineNumber, wire.Digest, recomputed);
            }

            store.Record(wire.Key, wire.Events);
        }

        return store;
    }

    private CassetteEntry? FindNearest(CassetteKey key, out IReadOnlyList<string>? differences)
    {
        // The nearest entry is the one with the fewest differing fields; ties keep the first recorded.
        CassetteEntry? best = null;
        IReadOnlyList<string>? bestDifferences = null;
        foreach (var candidate in _ordered)
        {
            var diff = key.Differences(candidate.Key);
            if (bestDifferences is null || diff.Count < bestDifferences.Count)
            {
                best = candidate;
                bestDifferences = diff;
            }
        }

        differences = bestDifferences;
        return best;
    }

    private static WireEntry ToWire(CassetteEntry entry) => new(entry.Digest, entry.Key, entry.Events);

    internal sealed record WireEntry(string Digest, CassetteKey Key, IReadOnlyList<ModelEvent> Events);
}

/// <summary>A request had no recorded entry. The message names the digest and the differences from the nearest entry.</summary>
public sealed class CassetteMismatchException : Exception
{
    public CassetteMismatchException(string requestDigest, string? nearestDigest, IReadOnlyList<string> differences, bool noEntryForProviderOrModel)
        : base(BuildMessage(requestDigest, nearestDigest, differences, noEntryForProviderOrModel))
    {
        RequestDigest = requestDigest;
        NearestDigest = nearestDigest;
        Differences = differences;
    }

    /// <summary>The digest of the request that found no entry.</summary>
    public string RequestDigest { get; }

    /// <summary>The digest of the nearest recorded entry, or null when the store is empty.</summary>
    public string? NearestDigest { get; }

    /// <summary>The key fields that differ from the nearest entry (all fields when the store is empty).</summary>
    public IReadOnlyList<string> Differences { get; }

    private static string BuildMessage(string requestDigest, string? nearestDigest, IReadOnlyList<string> differences, bool empty)
    {
        var message = new StringBuilder("No cassette matches request digest ").Append(requestDigest).Append(". ");
        if (empty || nearestDigest is null)
        {
            message.Append("The cassette store has no entries. ");
        }
        else
        {
            message.Append("Nearest entry ").Append(nearestDigest)
                .Append(" differs in: ").Append(string.Join(", ", differences)).Append(". ");
        }

        message.Append("Re-record the call explicitly; no live model is called.");
        return message.ToString();
    }
}

/// <summary>A recorded entry's digest does not match its key, so the key was changed after recording.</summary>
public sealed class CassetteTamperException(int lineNumber, string storedDigest, string recomputedDigest)
    : Exception($"Cassette line {lineNumber} fails its digest check: stored {storedDigest}, recomputed {recomputedDigest}. The record was changed after it was recorded.")
{
    public int LineNumber { get; } = lineNumber;

    public string StoredDigest { get; } = storedDigest;

    public string RecomputedDigest { get; } = recomputedDigest;
}
