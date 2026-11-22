using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// What gets published to an anchor sink: the journal's size, Merkle root and head hash at a moment in time
/// (doc 46 section 6.5.1). The record carries no journal content, only hashes. A sink that keeps it outside the
/// operator's control lets a later check detect a rewritten history.
/// </summary>
public sealed record AnchorRecord
{
    /// <summary>The only schema version this build writes and accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    public required int Version { get; init; }

    /// <summary>Identifier of the sink this anchor was written for (1 to 128 characters).</summary>
    public required string SinkId { get; init; }

    /// <summary>Number of journal records covered (at least 1). Records 0 to TreeSize-1 are anchored.</summary>
    public required long TreeSize { get; init; }

    /// <summary>Merkle root over the hashes of those records (32 bytes, base64 in JSON).</summary>
    public required byte[] Root { get; init; }

    /// <summary>Hash of the last covered record, the journal head at anchor time (32 bytes, base64 in JSON).</summary>
    public required byte[] JournalHead { get; init; }

    /// <summary>When the anchor was made, in UTC.</summary>
    public required DateTimeOffset AnchoredAt { get; init; }

    /// <summary>True when every field is within its allowed range. A record read from a sink should be checked before use.</summary>
    [JsonIgnore]
    public bool IsWellFormed =>
        Version == CurrentVersion
        && !string.IsNullOrEmpty(SinkId) && SinkId.Length <= 128
        && TreeSize >= 1
        && Root is { Length: MerkleHasher.HashLength }
        && JournalHead is { Length: MerkleHasher.HashLength }
        && AnchoredAt.Offset == TimeSpan.Zero;

    /// <summary>
    /// Builds an anchor over all leaves of <paramref name="tree"/>, whose last leaf is the record with hash
    /// <paramref name="journalHead"/>. The time is normalised to UTC.
    /// </summary>
    /// <exception cref="ArgumentException">The tree is empty or the head is not 32 bytes.</exception>
    public static AnchorRecord Create(string sinkId, MerkleTree tree, byte[] journalHead, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(journalHead);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (tree.Size == 0 || journalHead.Length != MerkleHasher.HashLength)
        {
            throw new ArgumentException("An anchor needs a non-empty tree and a 32-byte journal head.");
        }

        AnchorRecord record = new()
        {
            Version = CurrentVersion,
            SinkId = sinkId,
            TreeSize = tree.Size,
            Root = tree.Root(),
            JournalHead = [.. journalHead],
            AnchoredAt = timeProvider.GetUtcNow(),
        };
        return record.IsWellFormed ? record : throw new ArgumentException("Sink id must be 1 to 128 characters.", nameof(sinkId));
    }
}
