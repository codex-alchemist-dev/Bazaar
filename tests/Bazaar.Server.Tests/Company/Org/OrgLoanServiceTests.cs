using Bazaar.Contracts.Gc;
using Bazaar.Server.Company.Org;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Org;
using Bazaar.Server.Org.Data;
using Bazaar.Server.Tests.Org;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Company.Org;

/// <summary>
/// <see cref="OrgLoanService"/> against a real PostgreSQL holding migration 0018 (the existing <see cref="OrgDb"/> fixture).
/// The GC writer is a recording fake: the GC tables are not part of the 0018 fixture, so the tests check the request the
/// service hands to <see cref="IGcLogWriter"/>, including the idempotency key.
/// </summary>
public sealed class OrgLoanServiceTests(OrgDb db) : IClassFixture<OrgDb>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingWriter _writer = new();
    private readonly OrgActor _owner = new(AuthorKind.Owner, Guid.CreateVersion7());

    private OrgRelationRepository Repo => new(db.DataSource);

    private OrgLoanService Service() => new(Repo, _writer, new FakeTimeProvider(_now));

    private static StartLoanRequest Loan(Guid lent, Guid lead, Guid project) =>
        new(lent, lead, project, _now, _now.AddDays(14));

    [Fact]
    public async Task Starting_a_loan_stores_it_and_writes_one_line_with_the_idempotent_key()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var gc = Guid.CreateVersion7();

        var result = await Service().StartAsync(Loan(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), _owner, OrgSettings.Default, gc, Ct);

        Assert.Equal(OrgLoanOutcome.Applied, result.Outcome);
        var row = result.Relation!;
        Assert.Equal(OrgRelationKind.LentTo, row.Kind);
        Assert.Null(row.EndsAt);
        var line = Assert.Single(_writer.Requests);
        Assert.Equal(gc, line.GcId);
        Assert.Equal($"org:{row.Id:D}:loan_started", line.ClientMsgId);
        Assert.Equal(SourceTrust.Owner, line.SourceTrust);
        Assert.Contains(_now.AddDays(14).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), line.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_or_self_loan_writes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        var self = await Service().StartAsync(Loan(agent, agent, project), _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var byAgent = await Service().StartAsync(
            Loan(Guid.CreateVersion7(), agent, project), new OrgActor(AuthorKind.Agent, agent), OrgSettings.Default, Guid.CreateVersion7(), Ct);

        Assert.Equal(OrgLoanOutcome.Refused, self.Outcome);
        Assert.Equal(OrgLoanOutcome.Refused, byAgent.Outcome);
        Assert.Empty(_writer.Requests);
        Assert.Empty(await Repo.ListByScopeAsync(project, ct: Ct));
    }

    [Fact]
    public async Task A_second_live_loan_of_the_same_edge_is_a_conflict()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var request = Loan(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await Service().StartAsync(request, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);

        var again = await Service().StartAsync(request, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);

        Assert.Equal(OrgLoanOutcome.Conflict, again.Outcome);
        Assert.Single(_writer.Requests);
    }

    [Fact]
    public async Task Ending_a_loan_ends_the_row_and_says_open_cards_stay()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var started = await Service().StartAsync(
            Loan(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var id = started.Relation!.Id;
        var gc = Guid.CreateVersion7();

        var ended = await Service().EndAsync(id, started.Relation.Version, _owner, OrgSettings.Default, gc, Ct);

        Assert.Equal(OrgLoanOutcome.Applied, ended.Outcome);
        Assert.Equal(_now, ended.Relation!.EndsAt);
        Assert.Equal(2, ended.Relation.Version);
        var line = _writer.Requests[^1];
        Assert.Equal(gc, line.GcId);
        Assert.Equal($"org:{id:D}:loan_ended", line.ClientMsgId);
        Assert.Contains("stay_with_owner", line.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ending_with_a_stale_version_or_twice_does_not_write_a_line()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var started = await Service().StartAsync(
            Loan(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var id = started.Relation!.Id;
        var before = _writer.Requests.Count;

        var stale = await Service().EndAsync(id, 99, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        await Service().EndAsync(id, 1, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var twice = await Service().EndAsync(id, 2, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var missing = await Service().EndAsync(Guid.CreateVersion7(), 1, _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);

        Assert.Equal(OrgLoanOutcome.Conflict, stale.Outcome);
        Assert.Equal(OrgLoanOutcome.Invalid, twice.Outcome);
        Assert.Equal(OrgLoanOutcome.NotFound, missing.Outcome);
        Assert.Equal(before + 1, _writer.Requests.Count);
    }

    [Fact]
    public async Task The_project_lead_may_end_a_loan_when_the_setting_allows_it()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        await Repo.InsertAsync(new NewOrgRelation(Guid.CreateVersion7(), OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project, _now.AddDays(-5), _owner.PrincipalId), null, Ct);
        var started = await Service().StartAsync(Loan(Guid.CreateVersion7(), lead, project), _owner, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var leadActor = new OrgActor(AuthorKind.Agent, lead);

        var refused = await Service().EndAsync(started.Relation!.Id, 1, leadActor, OrgSettings.Default, Guid.CreateVersion7(), Ct);
        var allowed = await Service().EndAsync(started.Relation.Id, 1, leadActor, new OrgSettings(OrgRelationApprover.ProjectLead), Guid.CreateVersion7(), Ct);

        Assert.Equal(OrgLoanOutcome.Refused, refused.Outcome);
        Assert.Equal(OrgLoanOutcome.Applied, allowed.Outcome);
        Assert.Equal(SourceTrust.Agent, _writer.Requests[^1].SourceTrust);
    }

    private sealed class RecordingWriter : IGcLogWriter
    {
        public List<AppendRequest> Requests { get; } = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new AppendResult(request.GcId, Requests.Count, Guid.CreateVersion7(), false, _now));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
