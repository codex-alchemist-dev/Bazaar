using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>One stored last-writer-wins value.</summary>
/// <param name="WriteId">The write that stored the value.</param>
/// <param name="Hlc">The write's hybrid logical clock stamp.</param>
/// <param name="Version">The version text a client names as its base version.</param>
/// <param name="Payload">The stored payload.</param>
public sealed record LwwEntry(Guid WriteId, Hlc Hlc, string Version, ReadOnlyMemory<byte> Payload);

/// <summary>State the last-writer-wins rules need: write-id claims, the current value per key and its history.</summary>
public interface ILastWriterWinsState
{
    /// <summary>Atomically claims a write id. Returns false when the id was already claimed.</summary>
    ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken);

    /// <summary>The current value of a key, or null when none was ever stored.</summary>
    ValueTask<LwwEntry?> GetCurrentAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an entry for a key. When <paramref name="becomesCurrent"/> is true the entry replaces the
    /// current value and the previous current value moves to history. Otherwise the entry goes to history.
    /// </summary>
    ValueTask SaveAsync(string key, LwwEntry entry, bool becomesCurrent, CancellationToken cancellationToken);
}

/// <summary>
/// Shared last-writer-wins merge (doc 56 section 5, items 4 and 5). Per key, the highest HLC wins. The
/// value it replaces is kept in history. A write that loses is also kept in history, so the history
/// holds every non-current value and is the same set for any arrival order. A losing write is
/// outcome <see cref="OfflineOutcomeKind.Applied"/> with the current version, because nothing is refused.
/// </summary>
public abstract class LastWriterWinsRule : IOfflineMergeRule
{
    private readonly ILastWriterWinsState _state;

    /// <summary>Creates the rule over the given state.</summary>
    /// <param name="state">The last-writer-wins state.</param>
    protected LastWriterWinsRule(ILastWriterWinsState state)
    {
        _state = state;
    }

    /// <inheritdoc />
    public abstract OfflineDataClass DataClass { get; }

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (!IsWellFormed(write.Payload))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Payload is malformed for this data class.");
        }

        if (!await _state.TryClaimWriteAsync(write.WriteId, cancellationToken))
        {
            return RuleOutcomes.Duplicate(write.WriteId);
        }

        var current = await _state.GetCurrentAsync(write.Target, cancellationToken);
        var entry = new LwwEntry(write.WriteId, write.Hlc, FormatVersion(write.Hlc), write.Payload);

        var veto = await CheckAsync(write, current, cancellationToken);
        if (veto is not null)
        {
            return veto;
        }

        if (current is null || write.Hlc > current.Hlc)
        {
            await _state.SaveAsync(write.Target, entry, becomesCurrent: true, cancellationToken);
            return RuleOutcomes.Applied(write.WriteId, entry.Version);
        }

        await _state.SaveAsync(write.Target, entry, becomesCurrent: false, cancellationToken);
        return RuleOutcomes.Applied(write.WriteId, current.Version);
    }

    /// <summary>Checks the payload shape before anything is claimed. Defaults to accepting everything.</summary>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>False when the payload must be rejected.</returns>
    protected virtual bool IsWellFormed(ReadOnlyMemory<byte> payload) => true;

    /// <summary>
    /// A hook that can stop the last-writer-wins decision, for example by holding a change for review.
    /// Returns null to continue with the normal rule.
    /// </summary>
    /// <param name="write">The write being merged.</param>
    /// <param name="current">The current value of the key, or null.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    protected virtual ValueTask<OfflineWriteOutcome?> CheckAsync(OfflineWrite write, LwwEntry? current, CancellationToken cancellationToken) =>
        ValueTask.FromResult<OfflineWriteOutcome?>(null);

    /// <summary>Formats an HLC as a version text: wall time, counter and device, dot separated.</summary>
    /// <param name="hlc">The stamp.</param>
    /// <returns>The version text.</returns>
    public static string FormatVersion(Hlc hlc) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hlc.WallMillis}.{hlc.Counter}.{hlc.DeviceId:N}");
}

/// <summary>In-memory <see cref="ILastWriterWinsState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryLastWriterWinsState : ILastWriterWinsState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _claimed = [];
    private readonly Dictionary<string, (LwwEntry? Current, List<LwwEntry> History)> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_claimed.Add(writeId));
        }
    }

    /// <inheritdoc />
    public ValueTask<LwwEntry?> GetCurrentAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_keys.TryGetValue(key, out var slot) ? slot.Current : null);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(string key, LwwEntry entry, bool becomesCurrent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var slot = _keys.TryGetValue(key, out var existing) ? existing : (Current: null, History: new List<LwwEntry>());
            if (becomesCurrent)
            {
                if (slot.Current is not null)
                {
                    slot.History.Add(slot.Current);
                }

                slot.Current = entry;
            }
            else
            {
                slot.History.Add(entry);
            }

            _keys[key] = slot;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The values of a key that are no longer current, in the order they were stored.</summary>
    /// <param name="key">The key.</param>
    /// <returns>A snapshot of the history.</returns>
    public IReadOnlyList<LwwEntry> GetHistory(string key)
    {
        lock (_gate)
        {
            return _keys.TryGetValue(key, out var slot) ? [.. slot.History] : [];
        }
    }
}
