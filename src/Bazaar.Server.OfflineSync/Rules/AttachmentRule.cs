using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>State the attachment rule needs: content-addressed storage.</summary>
public interface IAttachmentState
{
    /// <summary>Stores bytes under their content hash when the hash is absent. Returns true when newly stored.</summary>
    ValueTask<bool> StoreIfAbsentAsync(string contentHash, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for content-addressed attachments (doc 52 section 5, item 6). The target is the
/// lowercase SHA-256 hex of the content. Identical bytes always merge to a no-op
/// <see cref="OfflineOutcomeKind.Applied"/>, including a second identical write.
/// </summary>
public sealed class AttachmentRule : IOfflineMergeRule
{
    private const int _hashHexLength = 64;

    private readonly IAttachmentState _state;

    /// <summary>Creates the rule over the given storage.</summary>
    /// <param name="state">The attachment storage.</param>
    public AttachmentRule(IAttachmentState state)
    {
        _state = state;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.Attachment;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (!IsSha256Hex(write.Target))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Attachment target must be a lowercase SHA-256 hex digest.");
        }

        await _state.StoreIfAbsentAsync(write.Target, write.Payload, cancellationToken);
        return RuleOutcomes.Applied(write.WriteId, write.Target);
    }

    private static bool IsSha256Hex(string target) =>
        target.Length == _hashHexLength && target.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>In-memory <see cref="IAttachmentState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryAttachmentState : IAttachmentState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> StoreIfAbsentAsync(string contentHash, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_blobs.ContainsKey(contentHash))
            {
                return ValueTask.FromResult(false);
            }

            _blobs[contentHash] = payload.ToArray();
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>The number of distinct stored attachments.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _blobs.Count;
            }
        }
    }
}
