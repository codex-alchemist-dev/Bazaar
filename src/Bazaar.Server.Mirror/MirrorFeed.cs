using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Mirror;

/// <summary>
/// Turns owner decisions into Mirror signals (doc 26 section 5). The signal id comes from the kind, the source reference, the topic
/// and the direction, so a replayed event is a no-op. Which kinds are learned from, and whether learning is paused, stay the
/// service's call.
/// </summary>
public sealed class MirrorFeed(IMirrorService service, TimeProvider time) : IMirrorFeed
{
    public async Task<bool> CardDecidedAsync(string cardRef, bool accepted, string? reason, IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        var kind = accepted ? MirrorSignalKind.CardAccepted : MirrorSignalKind.CardRejected;
        var direction = accepted ? MirrorDirection.Prefer : MirrorDirection.Avoid;
        return await RecordAllAsync(kind, cardRef, topics.Select(t => (t, direction)), reason, cancellationToken);
    }

    public Task<bool> OwnerEditedAsync(string sourceRef, IReadOnlyList<string> removedTopics, IReadOnlyList<string> addedTopics, string? note, CancellationToken cancellationToken) =>
        RecordAllAsync(
            MirrorSignalKind.OwnerEdit,
            sourceRef,
            removedTopics.Select(t => (t, MirrorDirection.Avoid)).Concat(addedTopics.Select(t => (t, MirrorDirection.Prefer))),
            note,
            cancellationToken);

    public Task<bool> ReviewCommentedAsync(string sourceRef, string topic, MirrorDirection direction, string comment, CancellationToken cancellationToken) =>
        RecordAllAsync(MirrorSignalKind.ReviewComment, sourceRef, [(topic, direction)], comment, cancellationToken);

    public Task<bool> CouncilTieBrokenAsync(string voteRef, string topic, bool approved, string? reasoning, CancellationToken cancellationToken) =>
        RecordAllAsync(MirrorSignalKind.CouncilTieBreak, voteRef, [(topic, approved ? MirrorDirection.Prefer : MirrorDirection.Avoid)], reasoning, cancellationToken);

    public Task<bool> PreferenceStatedAsync(string statementRef, string topic, MirrorDirection direction, string statement, CancellationToken cancellationToken) =>
        RecordAllAsync(MirrorSignalKind.PreferenceStatement, statementRef, [(topic, direction)], statement, cancellationToken);

    private async Task<bool> RecordAllAsync(
        MirrorSignalKind kind, string sourceRef, IEnumerable<(string Topic, MirrorDirection Direction)> items, string? payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        var learned = false;
        foreach (var (topic, direction) in items.Where(i => !string.IsNullOrWhiteSpace(i.Topic)).Distinct())
        {
            var signal = new MirrorSignal(DeterministicId(kind, sourceRef, topic, direction), kind, topic, direction, sourceRef, 1, payload ?? string.Empty, time.GetUtcNow());
            learned |= await service.RecordAsync(signal, ct);
        }

        return learned;
    }

    private static Guid DeterministicId(MirrorSignalKind kind, string sourceRef, string topic, MirrorDirection direction)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{sourceRef}\n{topic}\n{direction}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
