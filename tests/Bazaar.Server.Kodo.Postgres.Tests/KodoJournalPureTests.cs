using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo.Postgres.Tests;

/// <summary>The store's decisions without a database: range verification, the replay plan, state-change parsing and the proof builder.</summary>
public class KodoJournalPureTests
{
    [Fact]
    public void A_range_that_starts_mid_chain_verifies_when_linked()
    {
        List<JournalRecord> all = Chains.Build(10);

        Assert.Null(KodoJournalRangeVerifier.FirstBroken(all));
        Assert.Null(KodoJournalRangeVerifier.FirstBroken(all.GetRange(4, 5)));
        Assert.Null(KodoJournalRangeVerifier.FirstBroken([]));
    }

    [Fact]
    public void A_range_reports_the_first_record_that_does_not_verify()
    {
        List<JournalRecord> all = Chains.Build(6);
        List<JournalRecord> skipped = [all[2], all[4]];
        List<JournalRecord> tampered = [.. all];
        tampered[3] = tampered[3] with { Payload = [1, 2, 3] };
        List<JournalRecord> badGenesis = [all[0] with { PreviousHash = new byte[32].Select(_ => (byte)1).ToArray() }];

        Assert.Equal(1, KodoJournalRangeVerifier.FirstBroken(skipped));
        Assert.Equal(3, KodoJournalRangeVerifier.FirstBroken(tampered));
        Assert.Equal(0, KodoJournalRangeVerifier.FirstBroken(badGenesis));
    }

    [Fact]
    public void Into_an_empty_store_the_whole_run_is_written()
    {
        List<JournalRecord> run = Chains.Build(3);

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(null, new Dictionary<long, byte[]>(), run);

        Assert.Null(plan.Conflict);
        Assert.Equal(3, plan.ToInsert.Count);
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void An_empty_store_accepts_a_run_that_starts_later_because_a_gap_is_never_invented()
    {
        List<JournalRecord> run = Chains.Build(3, firstSequence: 40, previous: Chains.Build(1, 39)[0].Hash);

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(null, new Dictionary<long, byte[]>(), run);

        Assert.Null(plan.Conflict);
        Assert.Equal(3, plan.ToInsert.Count);
    }

    [Fact]
    public void Replaying_stored_records_writes_nothing_and_a_run_that_overlaps_writes_only_the_new_tail()
    {
        List<JournalRecord> all = Chains.Build(6);
        Dictionary<long, byte[]> stored = all.Take(4).ToDictionary(r => r.Sequence, r => r.Hash);

        KodoJournalAppendPlan again = KodoJournalAppendPlanner.Plan(all[3], stored, all.GetRange(0, 4));
        KodoJournalAppendPlan overlap = KodoJournalAppendPlanner.Plan(all[3], stored, all.GetRange(2, 4));

        Assert.Null(again.Conflict);
        Assert.Empty(again.ToInsert);
        Assert.Equal(4, again.Skipped);
        Assert.Null(overlap.Conflict);
        Assert.Equal([4L, 5L], overlap.ToInsert.Select(r => r.Sequence));
        Assert.Equal(2, overlap.Skipped);
    }

    [Fact]
    public void A_run_that_leaves_a_hole_after_the_head_is_a_gap()
    {
        List<JournalRecord> all = Chains.Build(8);

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(all[2], new Dictionary<long, byte[]>(), all.GetRange(5, 3));

        Assert.Equal(KodoJournalConflict.Gap, plan.Conflict);
    }

    [Fact]
    public void A_different_hash_at_a_stored_sequence_is_a_fork()
    {
        List<JournalRecord> mine = Chains.Build(4);
        List<JournalRecord> theirs = Chains.Build(4, summary: i => $"other {i}");
        Dictionary<long, byte[]> stored = theirs.ToDictionary(r => r.Sequence, r => r.Hash);

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(theirs[3], stored, mine);

        Assert.Equal(KodoJournalConflict.Diverged, plan.Conflict);
    }

    [Fact]
    public void A_new_record_that_does_not_link_to_the_head_is_a_fork()
    {
        List<JournalRecord> mine = Chains.Build(4);
        List<JournalRecord> theirs = Chains.Build(4, summary: i => $"other {i}");

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(theirs[1], new Dictionary<long, byte[]>(), mine.GetRange(2, 2));

        Assert.Equal(KodoJournalConflict.Diverged, plan.Conflict);
    }

    [Fact]
    public void A_broken_incoming_run_is_refused_before_anything_else()
    {
        List<JournalRecord> run = Chains.Build(3);
        run[1] = run[1] with { Payload = [9] };

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(null, new Dictionary<long, byte[]>(), run);

        Assert.Equal(KodoJournalConflict.BrokenChain, plan.Conflict);
    }

    [Fact]
    public void Replaying_a_plan_twice_is_idempotent()
    {
        List<JournalRecord> all = Chains.Build(5);
        Dictionary<long, byte[]> stored = [];
        JournalRecord? head = null;

        for (int round = 0; round < 2; round++)
        {
            KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(head, stored, all);
            Assert.Null(plan.Conflict);
            Assert.Equal(round == 0 ? 5 : 0, plan.ToInsert.Count);
            foreach (JournalRecord record in plan.ToInsert)
            {
                stored[record.Sequence] = record.Hash;
                head = record;
            }
        }
    }

    [Fact]
    public void A_state_record_yields_its_change_and_other_records_do_not()
    {
        JournalRecord state = Chains.Build(1, kind: "state", summary: _ => "state healthy -> degraded")[0];
        JournalRecord observation = Chains.Build(1, kind: "observation", summary: _ => "state healthy -> degraded")[0];
        JournalRecord odd = Chains.Build(1, kind: "state", summary: _ => "state healthy to degraded")[0];
        JournalRecord huge = Chains.Build(1, kind: "state", summary: _ => $"state {new string('a', 40)} -> ok")[0];

        KodoStateChange? change = KodoStateChangeParser.TryParse(state);

        Assert.Equal(new KodoStateChange(0, state.At, "healthy", "degraded"), change);
        Assert.Null(KodoStateChangeParser.TryParse(observation));
        Assert.Null(KodoStateChangeParser.TryParse(odd));
        Assert.Null(KodoStateChangeParser.TryParse(huge));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    public void Every_record_has_a_proof_that_verifies_against_the_root(int count)
    {
        List<byte[]> hashes = [.. Chains.Build(count).Select(r => r.Hash)];

        for (int sequence = 0; sequence < count; sequence++)
        {
            KodoJournalProofView proof = KodoJournalProofs.Build(sequence, hashes)!;
            MerkleInclusionProof inclusion = new(proof.Sequence, proof.TreeSize, [.. proof.Path.Select(Convert.FromHexString)]);

            Assert.Equal(count, proof.TreeSize);
            Assert.True(MerkleVerifier.VerifyInclusion(Convert.FromHexString(proof.RecordHash), inclusion, Convert.FromHexString(proof.Root)));
        }
    }

    [Fact]
    public void A_proof_outside_the_history_is_null()
    {
        List<byte[]> hashes = [.. Chains.Build(3).Select(r => r.Hash)];

        Assert.Null(KodoJournalProofs.Build(3, hashes));
        Assert.Null(KodoJournalProofs.Build(-1, hashes));
        Assert.Null(KodoJournalProofs.Build(0, []));
    }
}
