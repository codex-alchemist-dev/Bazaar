using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Health;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Tests.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Council.Tests.Health;

/// <summary>Vote reversals per project from the Council tables on real PostgreSQL (doc 25 morale signals).</summary>
public sealed class CouncilHealthFactSourceTests(CouncilDb db) : IClassFixture<CouncilDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset _day = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> ProjectGcAsync(string? project)
    {
        var gc = await db.CreateGcAsync();
        await using var cmd = db.DataSource.CreateCommand("UPDATE gc SET metadata = $1::jsonb WHERE gc_id = $2");
        cmd.Parameters.AddWithValue(project is null ? "{}" : $"{{\"project\":\"{project}\"}}");
        cmd.Parameters.AddWithValue(gc);
        await cmd.ExecuteNonQueryAsync(Ct);
        return gc;
    }

    /// <summary>A vote decided at <paramref name="decidedAt"/>; each voter is (independent choice, follow-up choice or null).</summary>
    private async Task<Guid> DecidedVoteAsync(Guid gc, DateTimeOffset decidedAt, params (string Voter, string Independent, string? Followup)[] voters)
    {
        var store = new CouncilStore(db.DataSource, new CouncilTestCipher());
        var vote = new CouncilVoteRecord(
            Guid.CreateVersion7(), gc, "task:1", VoteKind.ApproveReject, MotionClass.Ordinary, VoteState.Collecting, 1, [],
            decidedAt.AddDays(1), 0, new HashSet<string>(StringComparer.Ordinal), "proposer", "author", 1L, null, decidedAt.AddHours(-3), false);
        await store.InsertVoteAsync(vote, Ct);
        foreach (var (voter, independent, _) in voters)
            Assert.True(await store.TryInsertBallotAsync(new StoredBallot(vote.VoteId, voter, BallotPhase.Independent, independent, "r", false, decidedAt.AddHours(-2)), Ct));

        Assert.True(await store.TryTransitionAsync(vote.VoteId, 1, VoteState.Revealed, "cast", decidedAt.AddHours(-1), null, null, Ct));
        var version = 2L;
        if (voters.Any(v => v.Followup is not null))
        {
            Assert.True(await store.TryTransitionAsync(vote.VoteId, version++, VoteState.FollowUp, "follow-up", decidedAt.AddMinutes(-50), null, null, Ct));
            foreach (var (voter, _, followup) in voters.Where(v => v.Followup is not null))
                Assert.True(await store.TryInsertBallotAsync(new StoredBallot(vote.VoteId, voter, BallotPhase.FollowUp, followup!, "r", false, decidedAt.AddMinutes(-40)), Ct));
        }

        Assert.True(await store.TryTransitionAsync(vote.VoteId, version, VoteState.Decided, "decided", decidedAt, null, null, Ct));
        return vote.VoteId;
    }

    [Fact]
    public async Task A_changed_followup_choice_in_a_vote_decided_that_day_is_one_reversal()
    {
        SkipWithoutDatabase();
        var project = $"p-{Guid.NewGuid():N}";
        var gc = await ProjectGcAsync(project);
        var flipped = await DecidedVoteAsync(gc, _day.AddHours(10), ("a", "approve", "reject"), ("b", "approve", "approve"));
        await DecidedVoteAsync(gc, _day.AddHours(11), ("a", "approve", "approve"));
        await DecidedVoteAsync(gc, _day.AddHours(12), ("a", "approve", null));

        var facts = await new CouncilHealthFactSource(db.DataSource).ReadAsync(project, DateOnly.FromDateTime(_day.UtcDateTime), Ct);

        var reversal = Assert.Single(facts.VoteReversals);
        Assert.Equal(flipped.ToString("D"), reversal.VoteId);
    }

    [Fact]
    public async Task Another_day_or_another_project_has_no_reversals()
    {
        SkipWithoutDatabase();
        var project = $"p-{Guid.NewGuid():N}";
        var gc = await ProjectGcAsync(project);
        var other = await ProjectGcAsync($"q-{Guid.NewGuid():N}");
        await DecidedVoteAsync(gc, _day.AddHours(10), ("a", "approve", "reject"));
        await DecidedVoteAsync(other, _day.AddHours(10), ("a", "approve", "reject"));

        var source = new CouncilHealthFactSource(db.DataSource);

        Assert.Empty((await source.ReadAsync(project, DateOnly.FromDateTime(_day.UtcDateTime.AddDays(1)), Ct)).VoteReversals);
        Assert.Single((await source.ReadAsync(project, DateOnly.FromDateTime(_day.UtcDateTime), Ct)).VoteReversals);
    }

    [Fact]
    public async Task Projects_are_the_slugs_of_gcs_that_hold_votes_and_a_gc_without_a_slug_is_skipped()
    {
        SkipWithoutDatabase();
        var project = $"p-{Guid.NewGuid():N}";
        await DecidedVoteAsync(await ProjectGcAsync(project), _day.AddHours(9), ("a", "approve", null));
        await DecidedVoteAsync(await ProjectGcAsync(null), _day.AddHours(9), ("a", "approve", null));

        var projects = await new CouncilHealthProjectSource(db.DataSource).ListProjectsAsync(Ct);

        Assert.Contains(project, projects);
        Assert.DoesNotContain("", projects);
    }
}
