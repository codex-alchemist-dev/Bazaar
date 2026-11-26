using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.GeneticMemory;

/// <summary>The Postgres review queue against a real PostgreSQL, with the same expectations as the in-memory ledger.</summary>
public sealed class PgGmtLedgerTests(GmtDb db) : IClassFixture<GmtDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GmtCandidate Candidate(string title, string? path = null, GmtStatus status = GmtStatus.Staged) => new(
        Guid.NewGuid(), "child-1", "agent-new", GmtContentClass.Lesson, GmtLessonKind.Procedure, title, "Body\n", "trace/abc",
        false, GmtReviewer.Architect, status, _t0, path);

    private async Task<PgGmtLedger> FreshAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.ClearAsync();
        return new PgGmtLedger(db.DataSource);
    }

    [Fact]
    public async Task A_candidate_round_trips_with_every_field()
    {
        var ledger = await FreshAsync();
        var c = Candidate("Retry") with { Tainted = true, RequiredReviewer = GmtReviewer.Owner, ContentClass = GmtContentClass.ToolGrant, Kind = GmtLessonKind.Failure };

        await ledger.AddAsync(c, Ct);

        Assert.Equal(c, await ledger.GetAsync(c.Id, Ct));
    }

    [Fact]
    public async Task A_null_trace_and_path_round_trip_as_null()
    {
        var ledger = await FreshAsync();
        var c = Candidate("Plain") with { TraceRef = null };

        await ledger.AddAsync(c, Ct);

        Assert.Equal(c, await ledger.GetAsync(c.Id, Ct));
    }

    [Fact]
    public async Task Unknown_id_is_null_and_updating_it_returns_false()
    {
        var ledger = await FreshAsync();

        Assert.Null(await ledger.GetAsync(Guid.NewGuid(), Ct));
        Assert.False(await ledger.UpdateAsync(Candidate("Ghost"), Ct));
    }

    [Fact]
    public async Task Update_changes_status_text_and_path_and_list_keeps_staging_order()
    {
        var ledger = await FreshAsync();
        var first = Candidate("First");
        var second = Candidate("Second");
        await ledger.AddAsync(first, Ct);
        await ledger.AddAsync(second, Ct);

        var accepted = first with { Status = GmtStatus.Accepted, Title = "First edited", AcceptedPath = "agents/agent-new/memory/lessons/first-edited.md" };
        Assert.True(await ledger.UpdateAsync(accepted, Ct));

        var all = await ledger.ListAsync(Ct);
        Assert.Equal([accepted, second], all);
    }

    [Fact]
    public async Task Two_candidates_cannot_claim_the_same_accepted_path()
    {
        var ledger = await FreshAsync();
        const string path = "agents/agent-new/memory/lessons/x.md";
        await ledger.AddAsync(Candidate("A", path, GmtStatus.Accepted), Ct);

        await Assert.ThrowsAsync<PostgresException>(() => ledger.AddAsync(Candidate("B", path, GmtStatus.Accepted), Ct));
    }

    [Fact]
    public async Task The_service_runs_on_the_postgres_ledger_and_a_rejected_row_stays()
    {
        var ledger = await FreshAsync();
        var service = new GeneticMemoryService(ledger, new NoWrite(), new NoSecrets());
        var staged = await service.StageAsync(
            new GmtStageRequest("child-1", "agent-new", GmtContentClass.Lesson, GmtLessonKind.Procedure, "Retry", "Back off.", null, false, false, GmtTerms.None), Ct);

        Assert.True(staged.Succeeded);
        Assert.Single(await service.QueueAsync(GmtReviewer.Owner, Ct));
        Assert.True((await service.RejectAsync(staged.Id!.Value, GmtReviewer.Owner, Ct)).Succeeded);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
        Assert.Equal(GmtStatus.Rejected, (await ledger.GetAsync(staged.Id!.Value, Ct))!.Status);
    }

    private sealed class NoWrite : IGmtLessonWriter
    {
        public Task<GmtWriteResult> WriteAsync(GmtLessonWrite write, string writerAgentId, CancellationToken ct) =>
            Task.FromResult(new GmtWriteResult(GmtWriteStatus.Written));
    }

    private sealed class NoSecrets : IGmtSecretScanner
    {
        public bool ContainsSecret(string text) => false;
    }
}
