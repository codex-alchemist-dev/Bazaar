using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>One stored edit version of a GC message.</summary>
/// <param name="WriteId">The write that created the version.</param>
/// <param name="BaseVersion">The version the edit was made against.</param>
/// <param name="Sequence">Server sequence of the version; the highest is current.</param>
/// <param name="Payload">The edited message payload.</param>
public sealed record GcEditRow(Guid WriteId, string? BaseVersion, long Sequence, ReadOnlyMemory<byte> Payload);

/// <summary>The result of appending an edit version.</summary>
/// <param name="Sequence">Server sequence assigned to the new version.</param>
/// <param name="ConcurrentWithExisting">True when another edit version already had the same base version.</param>
public readonly record struct GcEditAppend(long Sequence, bool ConcurrentWithExisting);

/// <summary>State the GC edit rule needs: write-id claims and the per-message version rows.</summary>
public interface IGcEditState
{
    /// <summary>Atomically claims a write id. Returns false when the id was already claimed.</summary>
    ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends an edit version row and assigns its server sequence. Must check for an existing edit
    /// with the same base version in the same atomic step as the append.
    /// </summary>
    ValueTask<GcEditAppend> AppendVersionAsync(string messageId, string? baseVersion, Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>The current version of a message (highest sequence), or null when it has no edits.</summary>
    ValueTask<GcEditRow?> GetCurrentAsync(string messageId, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for GC message edits (doc 52 section 5, item 2). Each edit is a new version row. The
/// highest server sequence is current, so every edit stays. Two edits made against the same base
/// version are a <see cref="OfflineOutcomeKind.ConcurrentEdit"/>; both survive.
/// </summary>
public sealed class GcMessageEditRule : IOfflineMergeRule
{
    private readonly IGcEditState _state;

    /// <summary>Creates the rule over the given edit state.</summary>
    /// <param name="state">The GC edit state.</param>
    public GcMessageEditRule(IGcEditState state)
    {
        _state = state;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.GcMessageEdit;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (RulePayloadCodec.ReadGcMessage(write.Payload) is null)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Payload is not a GC message edit.");
        }

        if (!await _state.TryClaimWriteAsync(write.WriteId, cancellationToken))
        {
            return RuleOutcomes.Duplicate(write.WriteId);
        }

        var append = await _state.AppendVersionAsync(write.Target, write.BaseVersion, write.WriteId, write.Payload, cancellationToken);
        var kind = append.ConcurrentWithExisting ? OfflineOutcomeKind.ConcurrentEdit : OfflineOutcomeKind.Applied;
        var reason = append.ConcurrentWithExisting ? "Concurrent edit: both versions kept; the higher server sequence is current." : null;
        return new OfflineWriteOutcome(write.WriteId, kind, RuleVersions.Format(append.Sequence), reason);
    }
}

/// <summary>In-memory <see cref="IGcEditState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryGcEditState : IGcEditState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _claimed = [];
    private readonly Dictionary<string, List<GcEditRow>> _versions = new(StringComparer.Ordinal);
    private long _lastSequence;

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_claimed.Add(writeId));
        }
    }

    /// <inheritdoc />
    public ValueTask<GcEditAppend> AppendVersionAsync(string messageId, string? baseVersion, Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(messageId, out var rows))
            {
                rows = [];
                _versions[messageId] = rows;
            }

            var concurrent = rows.Exists(row => row.BaseVersion == baseVersion);
            var sequence = ++_lastSequence;
            rows.Add(new GcEditRow(writeId, baseVersion, sequence, payload.ToArray()));
            return ValueTask.FromResult(new GcEditAppend(sequence, concurrent));
        }
    }

    /// <inheritdoc />
    public ValueTask<GcEditRow?> GetCurrentAsync(string messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(messageId, out var rows) || rows.Count == 0)
            {
                return ValueTask.FromResult<GcEditRow?>(null);
            }

            return ValueTask.FromResult<GcEditRow?>(rows.MaxBy(row => row.Sequence));
        }
    }

    /// <summary>All stored edit versions of a message, in arrival order.</summary>
    /// <param name="messageId">The message id.</param>
    /// <returns>A snapshot of the version rows.</returns>
    public IReadOnlyList<GcEditRow> GetVersions(string messageId)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(messageId, out var rows) ? [.. rows] : [];
        }
    }
}
