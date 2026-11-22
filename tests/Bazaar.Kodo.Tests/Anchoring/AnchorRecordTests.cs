using System.Text;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Tests.Journal;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Anchoring;

public sealed class AnchorRecordTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    internal static AnchorRecord AnchorOf(List<JournalRecord> records, string sink = "paired-devices")
    {
        MerkleTree tree = new(records.Select(r => r.Hash));
        FakeTimeProvider time = new(_start);
        return AnchorRecord.Create(sink, tree, records[^1].Hash, time);
    }

    [Fact]
    public void CanonicalJsonIsCamelCaseCompactAndStable()
    {
        AnchorRecord anchor = new()
        {
            Version = 1,
            SinkId = "s1",
            TreeSize = 3,
            Root = new byte[32],
            JournalHead = new byte[32],
            AnchoredAt = _start,
        };

        string json = AnchorCodec.SerializeToString(anchor);
        string zero = Convert.ToBase64String(new byte[32]);

        Assert.Equal($"{{\"version\":1,\"sinkId\":\"s1\",\"treeSize\":3,\"root\":\"{zero}\",\"journalHead\":\"{zero}\",\"anchoredAt\":\"2026-11-19T08:00:00+00:00\"}}", json);
        Assert.Equal(AnchorCodec.Serialize(anchor), AnchorCodec.Serialize(anchor with { AnchoredAt = _start.ToOffset(TimeSpan.FromHours(5)) }));
    }

    [Fact]
    public void RoundTripPreservesEveryField()
    {
        AnchorRecord anchor = AnchorOf(JournalSamples.Chain(1, 20));

        AnchorRecord? parsed = AnchorCodec.TryDeserialize(AnchorCodec.Serialize(anchor));

        Assert.NotNull(parsed);
        Assert.Equal(anchor.Version, parsed.Version);
        Assert.Equal(anchor.SinkId, parsed.SinkId);
        Assert.Equal(anchor.TreeSize, parsed.TreeSize);
        Assert.Equal(anchor.Root, parsed.Root);
        Assert.Equal(anchor.JournalHead, parsed.JournalHead);
        Assert.Equal(anchor.AnchoredAt, parsed.AnchoredAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"sinkId\":\"s\",\"treeSize\":1,\"root\":\"AA==\",\"journalHead\":\"AA==\",\"anchoredAt\":\"2026-11-19T08:00:00+00:00\"}")]
    public void MalformedJsonIsRejectedWithoutThrowing(string json)
    {
        Assert.Null(AnchorCodec.TryDeserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void UnknownMembersAndBadValuesAreRejected()
    {
        AnchorRecord anchor = AnchorOf(JournalSamples.Chain(2, 5));
        string json = AnchorCodec.SerializeToString(anchor);

        Assert.Null(AnchorCodec.TryDeserialize(Encoding.UTF8.GetBytes(json.Replace("{", "{\"extra\":1,", StringComparison.Ordinal))));
        Assert.Null(AnchorCodec.TryDeserialize(Encoding.UTF8.GetBytes(json.Replace("\"treeSize\":5", "\"treeSize\":0", StringComparison.Ordinal))));
        Assert.Null(AnchorCodec.TryDeserialize(Encoding.UTF8.GetBytes(json.Replace("+00:00", "+02:00", StringComparison.Ordinal))));
    }

    [Fact]
    public void CreateRejectsEmptyTreeBadHeadAndBadSinkId()
    {
        FakeTimeProvider time = new(_start);
        MerkleTree empty = new([]);
        MerkleTree one = new(MerkleTreeTests.Hashes(1));

        Assert.Throws<ArgumentException>(() => AnchorRecord.Create("s", empty, new byte[32], time));
        Assert.Throws<ArgumentException>(() => AnchorRecord.Create("s", one, new byte[31], time));
        Assert.Throws<ArgumentException>(() => AnchorRecord.Create("", one, new byte[32], time));
        Assert.Throws<ArgumentException>(() => AnchorRecord.Create(new string('x', 129), one, new byte[32], time));
    }

    [Fact]
    public void CreateUsesTheInjectedClockInUtc()
    {
        FakeTimeProvider time = new(_start);
        time.Advance(TimeSpan.FromMinutes(7));

        AnchorRecord anchor = AnchorRecord.Create("s", new MerkleTree(MerkleTreeTests.Hashes(2)), new byte[32], time);

        Assert.Equal(_start.AddMinutes(7), anchor.AnchoredAt);
        Assert.Equal(TimeSpan.Zero, anchor.AnchoredAt.Offset);
    }
}
