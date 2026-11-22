namespace Bazaar.Tabula.Tests;

public class ChainTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static List<ChainEntry> Build(ChainId chain, int count)
    {
        List<ChainEntry> list = [];
        ChainEntry? head = null;
        for (int i = 0; i < count; i++)
        {
            head = ChainHasher.Append(chain, head, _t0.AddMinutes(i), "user:a", null, "mod.thing.done", $"{{\"n\":{i}}}");
            list.Add(head);
        }

        return list;
    }

    [Fact]
    public void Hash_is_deterministic_and_normalises_time_zone()
    {
        ChainEntry a = ChainHasher.Append(ChainId.Default, null, _t0, "user:a", null, "mod.thing.done", "{}");
        ChainEntry b = ChainHasher.Append(ChainId.Default, null, _t0.ToOffset(TimeSpan.FromHours(5)), "user:a", null, "mod.thing.done", "{}");

        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(32, a.Hash.Length);
        Assert.Equal(1, a.Seq);
        Assert.All(a.PrevHash, x => Assert.Equal(0, x));
    }

    [Fact]
    public void Every_field_changes_the_hash()
    {
        ChainEntry baseline = ChainHasher.Append(ChainId.Default, null, _t0, "user:a", null, "mod.thing.done", "{}");
        byte[] prev = ChainHasher.GenesisHash();
        byte[][] others =
        [
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0.AddTicks(1), "user:a", null, "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:b", null, "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:a", "agent:x", "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:a", string.Empty, "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:a", null, "mod.thing.other", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:a", null, "mod.thing.done", "{ }", prev),
            ChainHasher.ComputeHash(ChainId.Default, 2, _t0, "user:a", null, "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(new ChainId("other"), 1, _t0, "user:a", null, "mod.thing.done", "{}", prev),
            ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "user:a", null, "mod.thing.done", "{}", prev, "other.domain"),
        ];

        Assert.All(others, h => Assert.NotEqual(baseline.Hash, h));
        Assert.Equal(others.Length, others.Select(Convert.ToHexString).Distinct().Count());
    }

    [Fact]
    public void Length_prefixing_blocks_field_boundary_forgery()
    {
        byte[] prev = ChainHasher.GenesisHash();
        byte[] x = ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "ab", null, "c.d", "{}", prev);
        byte[] y = ChainHasher.ComputeHash(ChainId.Default, 1, _t0, "a", null, "bc.d", "{}", prev);

        Assert.NotEqual(x, y);
    }

    [Fact]
    public void Valid_chain_verifies_and_matches_expected_head()
    {
        List<ChainEntry> chain = Build(ChainId.Default, 5);

        Assert.True(ChainVerifier.Verify(ChainId.Default, chain).IsValid);
        Assert.True(ChainVerifier.Verify(ChainId.Default, chain, chain[^1].Hash).IsValid);
        Assert.True(ChainVerifier.Verify(ChainId.Default, []).IsValid);
    }

    [Fact]
    public void Tampered_payload_is_detected_at_its_position()
    {
        List<ChainEntry> chain = Build(ChainId.Default, 5);
        chain[2] = chain[2] with { Payload = "{\"n\":99}" };

        ChainVerification result = ChainVerifier.Verify(ChainId.Default, chain);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FailedIndex);
        Assert.Equal(ChainFailure.HashMismatch, result.Failure);
    }

    [Fact]
    public void Rehashed_tamper_breaks_the_next_link()
    {
        List<ChainEntry> chain = Build(ChainId.Default, 4);
        ChainEntry forged = chain[1] with { Payload = "{\"n\":99}" };
        chain[1] = forged with { Hash = ChainHasher.ComputeHash(forged) };

        ChainVerification result = ChainVerifier.Verify(ChainId.Default, chain);

        Assert.Equal(2, result.FailedIndex);
        Assert.Equal(ChainFailure.PrevHashMismatch, result.Failure);
    }

    [Fact]
    public void Removed_middle_entry_and_reorder_are_detected()
    {
        List<ChainEntry> gap = Build(ChainId.Default, 4);
        gap.RemoveAt(1);
        Assert.Equal(ChainFailure.SequenceBroken, ChainVerifier.Verify(ChainId.Default, gap).Failure);

        List<ChainEntry> swapped = Build(ChainId.Default, 4);
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        Assert.False(ChainVerifier.Verify(ChainId.Default, swapped).IsValid);
    }

    [Fact]
    public void Truncated_tail_fails_only_against_an_expected_head()
    {
        List<ChainEntry> chain = Build(ChainId.Default, 5);
        byte[] head = chain[^1].Hash;
        chain.RemoveAt(4);

        Assert.True(ChainVerifier.Verify(ChainId.Default, chain).IsValid);
        Assert.Equal(ChainFailure.HeadMismatch, ChainVerifier.Verify(ChainId.Default, chain, head).Failure);
    }

    [Fact]
    public void Named_chains_are_independent_and_entries_cannot_move_between_them()
    {
        ChainId a = new("audit.security");
        ChainId b = new("audit.data");
        List<ChainEntry> chainA = Build(a, 3);
        List<ChainEntry> chainB = Build(b, 3);

        Assert.NotEqual(chainA[0].Hash, chainB[0].Hash);

        Dictionary<ChainId, ChainVerification> all = new(ChainVerifier.VerifyAll([.. chainA, .. chainB]));
        Assert.True(all[a].IsValid);
        Assert.True(all[b].IsValid);

        // Tamper with chain B only: A still verifies.
        chainB[1] = chainB[1] with { Kind = "mod.thing.changed" };
        all = new(ChainVerifier.VerifyAll([.. chainA, .. chainB]));
        Assert.True(all[a].IsValid);
        Assert.False(all[b].IsValid);

        // An entry from chain A placed into chain B is refused, and relabelling it breaks its hash.
        Assert.Equal(ChainFailure.WrongChain, ChainVerifier.Verify(b, [chainA[0]]).Failure);
        Assert.Equal(ChainFailure.HashMismatch, ChainVerifier.Verify(b, [chainA[0] with { Chain = b }]).Failure);
    }

    [Fact]
    public void Append_rejects_a_head_from_another_chain()
    {
        ChainEntry head = ChainHasher.Append(new ChainId("one"), null, _t0, "user:a", null, "mod.thing.done", "{}");

        Assert.Throws<ArgumentException>(() =>
            ChainHasher.Append(new ChainId("two"), head, _t0, "user:a", null, "mod.thing.done", "{}"));
    }

    [Fact]
    public void Verify_can_start_from_a_checkpoint()
    {
        List<ChainEntry> chain = Build(ChainId.Default, 6);
        List<ChainEntry> tail = chain.Skip(3).ToList();

        Assert.True(ChainVerifier.VerifyFrom(ChainId.Default, tail, 4, chain[2].Hash).IsValid);
        Assert.False(ChainVerifier.VerifyFrom(ChainId.Default, tail, 4, ChainHasher.GenesisHash()).IsValid);
    }

    [Fact]
    public void Chain_id_validates_names_and_defaults()
    {
        Assert.Equal("default", default(ChainId).Value);
        Assert.Equal(default(ChainId).Value, ChainId.Default.Value);
        Assert.Throws<ArgumentException>(() => new ChainId("Bad Name"));
        Assert.Throws<ArgumentException>(() => new ChainId(string.Empty));
    }
}
