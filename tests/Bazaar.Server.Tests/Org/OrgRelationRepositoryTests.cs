using Bazaar.Server.Org;
using Bazaar.Server.Org.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Org;

/// <summary>SQL repository round trips for <c>org_relation</c> on real PostgreSQL (doc 24 section 2, WP ORG1).</summary>
public sealed class OrgRelationRepositoryTests(OrgDb db) : IClassFixture<OrgDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private OrgRelationRepository Repo => new(db.DataSource);

    private static NewOrgRelation Relation(OrgRelationKind kind, Guid from, Guid to, Guid? project) =>
        new(Guid.CreateVersion7(), kind, from, to, project, _start, Guid.CreateVersion7());

    [Theory]
    [InlineData(OrgRelationKind.ReportsTo, false)]
    [InlineData(OrgRelationKind.LentTo, true)]
    [InlineData(OrgRelationKind.ReportsTo, true)]
    public async Task Insert_then_get_round_trips(OrgRelationKind kind, bool inProject)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var project = inProject ? Guid.CreateVersion7() : (Guid?)null;
        var draft = Relation(kind, Guid.CreateVersion7(), Guid.CreateVersion7(), project);

        var inserted = await Repo.InsertAsync(draft, null, Ct);
        var read = await Repo.GetAsync(draft.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(inserted, read);
        Assert.Equal(draft.Id, read.Id);
        Assert.Equal(kind, read.Kind);
        Assert.Equal(draft.FromAgentId, read.FromAgentId);
        Assert.Equal(draft.ToAgentId, read.ToAgentId);
        Assert.Equal(project, read.ProjectId);
        Assert.Equal(_start, read.StartsAt);
        Assert.Null(read.EndsAt);
        Assert.Equal(draft.CreatedBy, read.CreatedBy);
        Assert.Equal(1, read.Version);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Assert.Null(await Repo.GetAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Ending_with_the_current_version_returns_the_ended_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var row = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), null, Ct);
        var endsAt = _start.AddDays(14);

        var ended = await Repo.EndAsync(row.Id, row.Version, endsAt, null, Ct);

        Assert.NotNull(ended);
        Assert.Equal(endsAt, ended.EndsAt);
        Assert.Equal(row.Version + 1, ended.Version);
        Assert.Equal(ended, await Repo.GetAsync(row.Id, Ct));
    }

    [Fact]
    public async Task Ending_with_a_stale_version_changes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var row = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), null, Ct);

        var result = await Repo.EndAsync(row.Id, row.Version + 5, _start.AddDays(1), null, Ct);

        Assert.Null(result);
        Assert.Equal(row, await Repo.GetAsync(row.Id, Ct));
    }

    [Fact]
    public async Task Ending_an_ended_row_returns_null()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var row = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), null, Ct);
        var ended = await Repo.EndAsync(row.Id, row.Version, _start.AddDays(1), null, Ct);

        var again = await Repo.EndAsync(row.Id, ended!.Version, _start.AddDays(2), null, Ct);

        Assert.Null(again);
    }

    [Fact]
    public async Task List_by_scope_hides_ended_rows_unless_asked_and_treats_null_as_company_wide()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var project = Guid.CreateVersion7();
        var live = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), project), null, Ct);
        var toEnd = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, Guid.CreateVersion7(), Guid.CreateVersion7(), project), null, Ct);
        await Repo.EndAsync(toEnd.Id, toEnd.Version, _start.AddDays(1), null, Ct);
        var companyWide = await Repo.InsertAsync(Relation(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), Guid.CreateVersion7(), null), null, Ct);

        var inProject = await Repo.ListByScopeAsync(project, includeEnded: false, Ct);
        var inProjectWithEnded = await Repo.ListByScopeAsync(project, includeEnded: true, Ct);
        var company = await Repo.ListByScopeAsync(null, includeEnded: false, Ct);

        Assert.Equal([live.Id], inProject.Select(r => r.Id));
        Assert.Equal(2, inProjectWithEnded.Count);
        Assert.Contains(companyWide.Id, company.Select(r => r.Id));
        Assert.All(company, r => Assert.Null(r.ProjectId));
    }

    [Fact]
    public async Task List_from_agent_filters_by_kind()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var subject = Guid.CreateVersion7();
        var reports = await Repo.InsertAsync(Relation(OrgRelationKind.ReportsTo, subject, Guid.CreateVersion7(), null), null, Ct);
        var loan = await Repo.InsertAsync(Relation(OrgRelationKind.LentTo, subject, Guid.CreateVersion7(), Guid.CreateVersion7()), null, Ct);

        var all = await Repo.ListFromAgentAsync(subject, null, false, Ct);
        var onlyReports = await Repo.ListFromAgentAsync(subject, OrgRelationKind.ReportsTo, false, Ct);

        Assert.Equal(new[] { reports.Id, loan.Id }.Order(), all.Select(r => r.Id).Order());
        Assert.Equal([reports.Id], onlyReports.Select(r => r.Id));
    }
}
