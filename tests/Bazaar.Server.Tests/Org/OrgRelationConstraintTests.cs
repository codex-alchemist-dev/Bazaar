using Bazaar.Server.Org;
using Bazaar.Server.Org.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Org;

/// <summary>Uniqueness, CHECK and cycle rules of <c>org_relation</c> (migration 0018, doc 24 section 2) on real PostgreSQL.</summary>
public sealed class OrgRelationConstraintTests(OrgDb db) : IClassFixture<OrgDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private OrgRelationRepository Repo => new(db.DataSource);

    private static NewOrgRelation Relation(OrgRelationKind kind, Guid from, Guid to, Guid? project) =>
        new(Guid.CreateVersion7(), kind, from, to, project, _start, Guid.CreateVersion7());

    private Task<OrgRelation> InsertAsync(NewOrgRelation relation) => Repo.InsertAsync(relation, null, Ct);

    private static async Task<PostgresException> RejectedAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<PostgresException>(action);

    [Fact]
    public async Task Duplicate_live_loan_in_one_project_is_rejected()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, project) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(Relation(OrgRelationKind.LentTo, a, b, project));

        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.LentTo, a, b, project)));

        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task Second_live_reports_to_for_one_subject_and_scope_is_rejected()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));

        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.ReportsTo, a, c, null)));

        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task Ended_loan_can_be_lent_again_in_the_same_project()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, project) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var first = await InsertAsync(Relation(OrgRelationKind.LentTo, a, b, project));
        await Repo.EndAsync(first.Id, first.Version, _start.AddDays(1), null, Ct);

        var again = await InsertAsync(Relation(OrgRelationKind.LentTo, a, b, project));

        Assert.Equal(OrgRelationKind.LentTo, again.Kind);
        Assert.Null(again.EndsAt);
    }

    [Fact]
    public async Task One_subject_may_report_to_different_managers_in_different_projects()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();

        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, p1));
        var inOtherProject = await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, c, p2));
        var companyWide = await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));

        Assert.Null(inOtherProject.EndsAt);
        Assert.Null(companyWide.ProjectId);
    }

    [Fact]
    public async Task Reports_to_cycle_within_one_scope_is_rejected()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, b, c, null));

        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.ReportsTo, c, a, null)));

        Assert.Equal("23514", ex.SqlState);
        Assert.Contains("cycle", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_to_cycle_is_checked_inside_a_project_scope()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var project = Guid.CreateVersion7();
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, project));
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, b, c, project));

        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.ReportsTo, c, a, project)));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Cycle_across_scopes_is_not_a_cycle()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var project = Guid.CreateVersion7();
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));

        // B reports to A only inside the project; the company-wide edge A -> B is a different scope.
        var inProject = await InsertAsync(Relation(OrgRelationKind.ReportsTo, b, a, project));

        Assert.Equal(project, inProject.ProjectId);
    }

    [Fact]
    public async Task Ended_reports_to_edge_does_not_count_toward_a_cycle()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));
        var bToC = await InsertAsync(Relation(OrgRelationKind.ReportsTo, b, c, null));
        await Repo.EndAsync(bToC.Id, bToC.Version, _start.AddDays(1), null, Ct);

        var closing = await InsertAsync(Relation(OrgRelationKind.ReportsTo, c, a, null));

        Assert.Equal(OrgRelationKind.ReportsTo, closing.Kind);
    }

    [Fact]
    public async Task Reports_to_self_is_rejected_by_check()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var a = Guid.CreateVersion7();

        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.ReportsTo, a, a, null)));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Unknown_kind_is_rejected_by_check()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var ex = await RejectedAsync(async () =>
        {
            await using var cmd = db.DataSource.CreateCommand("""
                INSERT INTO org_relation (id, kind, from_agent_id, to_agent_id, starts_at, created_by)
                VALUES ($1, 'mentors', $2, $3, now(), $4)
                """);
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            await cmd.ExecuteNonQueryAsync(Ct);
        });

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Loan_without_a_project_is_rejected_by_check()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var ex = await RejectedAsync(() => InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), null)));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Non_cyclic_loans_and_a_reports_to_chain_are_accepted()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var project = Guid.CreateVersion7();

        var loanAB = await InsertAsync(Relation(OrgRelationKind.LentTo, a, b, project));
        var loanBC = await InsertAsync(Relation(OrgRelationKind.LentTo, b, c, project));
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, a, b, null));
        await InsertAsync(Relation(OrgRelationKind.ReportsTo, b, c, null));

        Assert.Equal(OrgRelationKind.LentTo, loanAB.Kind);
        Assert.Equal(OrgRelationKind.LentTo, loanBC.Kind);
    }

    [Fact]
    public async Task Concurrent_writes_that_would_close_a_cycle_admit_only_one()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        for (var round = 0; round < 5; round++)
        {
            var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            var project = Guid.CreateVersion7();
            var outcomes = await Task.WhenAll(
                TrySqlStateAsync(Relation(OrgRelationKind.ReportsTo, a, b, project)),
                TrySqlStateAsync(Relation(OrgRelationKind.ReportsTo, b, a, project)));

            Assert.Equal(1, outcomes.Count(o => o is null));
            Assert.Contains("23514", outcomes);
        }
    }

    [Fact]
    public async Task Ended_row_is_read_only()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var row = await InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));
        await Repo.EndAsync(row.Id, row.Version, _start.AddDays(1), null, Ct);

        var ex = await RejectedAsync(async () =>
        {
            await using var cmd = db.DataSource.CreateCommand("UPDATE org_relation SET ends_at = $1 WHERE id = $2");
            cmd.Parameters.AddWithValue(_start.AddDays(2));
            cmd.Parameters.AddWithValue(row.Id);
            await cmd.ExecuteNonQueryAsync(Ct);
        });

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Identity_fields_cannot_change_after_insert()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        SkipWithoutDatabase();
        var row = await InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));

        var ex = await RejectedAsync(async () =>
        {
            await using var cmd = db.DataSource.CreateCommand("UPDATE org_relation SET to_agent_id = $1 WHERE id = $2");
            cmd.Parameters.AddWithValue(Guid.CreateVersion7());
            cmd.Parameters.AddWithValue(row.Id);
            await cmd.ExecuteNonQueryAsync(Ct);
        });

        Assert.Equal("23514", ex.SqlState);
    }

    /// <summary>Inserts one relation on its own connection and returns its SQLSTATE, or null when it was stored.</summary>
    private async Task<string?> TrySqlStateAsync(NewOrgRelation relation)
    {
        try
        {
            await InsertAsync(relation);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
}
