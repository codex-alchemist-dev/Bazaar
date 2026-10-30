using Bazaar.Server.Council;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Council.Data;

/// <summary>Council tables (migration 0019) and <see cref="CouncilStore"/> on real PostgreSQL (doc 10, WP3).</summary>
public sealed class CouncilStoreTests(CouncilDb db) : IClassFixture<CouncilDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _opened = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _due = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private CouncilStore NewStore() => new(db.DataSource, new CouncilTestCipher());

    private static CouncilVoteRecord NewVote(Guid gcId, VoteKind kind = VoteKind.ApproveReject, IReadOnlyList<string>? options = null) => new(
        Guid.CreateVersion7(),
        gcId,
        "task:42",
        kind,
        MotionClass.Ordinary,
        VoteState.Collecting,
        1,
        options ?? [],
        _due,
        0,
        new HashSet<string>(StringComparer.Ordinal) { "agent-a", "agent-b", "agent-c" },
        "agent-proposer",
        "agent-author",
        987654321L,
        null,
        _opened);

    private static StoredBallot NewBallot(Guid voteId, string voter, BallotPhase phase, string choice, string reasoning, DateTimeOffset at) =>
        new(voteId, voter, phase, choice, reasoning, TaintedAtWrite: false, SubmittedAt: at);

    private static async Task<CouncilVoteRecord> InsertVoteAsync(CouncilStore store, Guid gcId, VoteKind kind = VoteKind.ApproveReject, IReadOnlyList<string>? options = null)
    {
        var vote = NewVote(gcId, kind, options);
        await store.InsertVoteAsync(vote, Ct);
        return vote;
    }

    private async Task<List<(long Version, string? From, string To, string Reason)>> TransitionsAsync(Guid voteId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT version, from_state, to_state, reason FROM council_transition WHERE vote_id = $1 ORDER BY version");
        cmd.Parameters.AddWithValue(voteId);
        var rows = new List<(long, string?, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task Insert_then_find_round_trips_the_vote()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var gcId = await db.CreateGcAsync();
        var vote = NewVote(gcId, VoteKind.OptionChoice, ["alpha", "beta", "gamma"]);

        await store.InsertVoteAsync(vote, Ct);
        var read = await store.FindVoteAsync(vote.VoteId, Ct);

        Assert.NotNull(read);
        Assert.Equal(vote.VoteId, read.VoteId);
        Assert.Equal(gcId, read.GcId);
        Assert.Equal("task:42", read.SubjectRef);
        Assert.Equal(VoteKind.OptionChoice, read.Kind);
        Assert.Equal(MotionClass.Ordinary, read.MotionClass);
        Assert.Equal(VoteState.Collecting, read.State);
        Assert.Equal(1, read.Version);
        Assert.Equal(["alpha", "beta", "gamma"], read.Options);
        Assert.Equal(_due, read.DeadlineAt);
        Assert.Equal(0, read.Extensions);
        Assert.True(vote.EligibleVoterIds.SetEquals(read.EligibleVoterIds));
        Assert.Equal("agent-proposer", read.ProposerId);
        Assert.Equal("agent-author", read.SubjectAuthorId);
        Assert.Equal(987654321L, read.RevealSeed);
        Assert.Null(read.VaultDecisionNo);
        Assert.Equal(_opened, read.CreatedAt);

        // Opening writes the first transition in the same transaction: null to Collecting, reason "open".
        Assert.Equal([(1L, (string?)null, "collecting", "open")], await TransitionsAsync(vote.VoteId));
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_vote()
    {
        SkipWithoutDatabase();
        Assert.Null(await NewStore().FindVoteAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Transition_with_the_expected_version_succeeds_and_appends_a_transition_row()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());
        var newDeadline = _due.AddHours(24);

        var moved = await store.TryTransitionAsync(vote.VoteId, 1, VoteState.Revealed, "all eligible cast", _opened.AddHours(2), newDeadline, 1, Ct);

        Assert.True(moved);
        var read = await store.FindVoteAsync(vote.VoteId, Ct);
        Assert.NotNull(read);
        Assert.Equal(VoteState.Revealed, read.State);
        Assert.Equal(2, read.Version);
        Assert.Equal(newDeadline, read.DeadlineAt);
        Assert.Equal(1, read.Extensions);
        Assert.Equal(
            [(1L, (string?)null, "collecting", "open"), (2L, "collecting", "revealed", "all eligible cast")],
            await TransitionsAsync(vote.VoteId));
    }

    [Fact]
    public async Task Transition_without_deadline_or_extension_keeps_the_stored_values()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());

        Assert.True(await store.TryTransitionAsync(vote.VoteId, 1, VoteState.Escalated, "tie", _opened, null, null, Ct));

        var read = await store.FindVoteAsync(vote.VoteId, Ct);
        Assert.NotNull(read);
        Assert.Equal(VoteState.Escalated, read.State);
        Assert.Equal(_due, read.DeadlineAt);
        Assert.Equal(0, read.Extensions);
    }

    [Fact]
    public async Task Transition_with_a_stale_version_returns_false_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());
        Assert.True(await store.TryTransitionAsync(vote.VoteId, 1, VoteState.Revealed, "reveal", _opened, null, null, Ct));

        // Version 1 is stale now (the row is at 2).
        var moved = await store.TryTransitionAsync(vote.VoteId, 1, VoteState.Withdrawn, "late withdraw", _opened, null, null, Ct);

        Assert.False(moved);
        var read = await store.FindVoteAsync(vote.VoteId, Ct);
        Assert.NotNull(read);
        Assert.Equal(VoteState.Revealed, read.State);
        Assert.Equal(2, read.Version);
        Assert.Equal(2, (await TransitionsAsync(vote.VoteId)).Count);
    }

    [Fact]
    public async Task Transition_on_an_unknown_vote_returns_false()
    {
        SkipWithoutDatabase();
        Assert.False(await NewStore().TryTransitionAsync(Guid.CreateVersion7(), 1, VoteState.Revealed, "x", _opened, null, null, Ct));
    }

    [Fact]
    public async Task Second_ballot_for_the_same_phase_is_rejected_and_writes_one_version()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());

        var first = await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.Independent, "yes", "Scope is clear.", _opened), Ct);
        var second = await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.Independent, "no", "Changed my mind.", _opened.AddMinutes(1)), Ct);

        Assert.True(first);
        Assert.False(second);
        var ballots = await store.ListBallotsAsync(vote.VoteId, Ct);
        Assert.Single(ballots);
        Assert.Equal("yes", ballots[0].Choice);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM council_ballot_ver WHERE vote_id = $1", vote.VoteId));
    }

    [Fact]
    public async Task Ballots_are_listed_in_submit_order()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());

        // Written out of voter order; the list must follow submit time.
        await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-c", BallotPhase.Independent, "yes", "c", _opened.AddSeconds(30)), Ct);
        await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.Independent, "no", "a", _opened.AddSeconds(10)), Ct);
        await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-b", BallotPhase.Independent, "yes", "b", _opened.AddSeconds(20)), Ct);

        var order = (await store.ListBallotsAsync(vote.VoteId, Ct)).Select(b => b.VoterId).ToList();
        Assert.Equal(["agent-a", "agent-b", "agent-c"], order);
    }

    [Fact]
    public async Task Reasoning_is_stored_sealed_and_round_trips_through_the_cipher()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());
        const string reasoning = "The rollback plan covers the migration. Ship it.";

        await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.Independent, "yes", reasoning, _opened), Ct);

        var listed = await store.ListBallotsAsync(vote.VoteId, Ct);
        Assert.Equal(reasoning, Assert.Single(listed).Reasoning);

        // The stored bytes are ciphertext: the plaintext does not appear in the column.
        var plaintextOffset = await db.ScalarAsync<int>(
            "SELECT position(convert_to($1, 'UTF8') in reasoning_ct) FROM council_ballot WHERE vote_id = $2 AND voter_id = $3",
            "rollback plan", vote.VoteId, "agent-a");
        Assert.Equal(0, plaintextOffset);
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM council_ballot_ver WHERE vote_id = $1 AND voter_id = $2 AND seq = 1", vote.VoteId, "agent-a"));
    }

    [Fact]
    public async Task Follow_up_phase_is_stored_separately_from_the_independent_ballot()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());

        Assert.True(await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.Independent, "no", "First view.", _opened), Ct));
        Assert.True(await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.FollowUp, "yes", "After the reveal.", _opened.AddMinutes(5)), Ct));

        var ballots = await store.ListBallotsAsync(vote.VoteId, Ct);
        Assert.Equal(2, ballots.Count);
        Assert.Equal(BallotPhase.Independent, ballots[0].Phase);
        Assert.Equal("no", ballots[0].Choice);
        Assert.Equal("First view.", ballots[0].Reasoning);
        Assert.Equal(BallotPhase.FollowUp, ballots[1].Phase);
        Assert.Equal("yes", ballots[1].Choice);
        Assert.Equal("After the reveal.", ballots[1].Reasoning);

        // A second follow-up is rejected; the independent row is untouched.
        Assert.False(await store.TryInsertBallotAsync(NewBallot(vote.VoteId, "agent-a", BallotPhase.FollowUp, "no", "Again.", _opened.AddMinutes(6)), Ct));
        Assert.Equal(2, (await store.ListBallotsAsync(vote.VoteId, Ct)).Count);
    }

    [Fact]
    public async Task Vault_decision_number_is_set_once_and_repeats_are_idempotent()
    {
        SkipWithoutDatabase();
        var store = NewStore();
        var vote = await InsertVoteAsync(store, await db.CreateGcAsync());

        await store.SetVaultDecisionNoAsync(vote.VoteId, 7, Ct);
        await store.SetVaultDecisionNoAsync(vote.VoteId, 7, Ct);

        Assert.Equal(7, (await store.FindVoteAsync(vote.VoteId, Ct))!.VaultDecisionNo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetVaultDecisionNoAsync(vote.VoteId, 8, Ct));
    }
}
