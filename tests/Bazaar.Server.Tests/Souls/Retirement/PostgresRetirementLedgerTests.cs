using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Souls.Retirement;

public sealed class PostgresRetirementLedgerTests(RetirementDb db) : IClassFixture<RetirementDb>
{
    [Fact]
    public async Task Records_and_reads_latest_outcome_per_step()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ledger = new PostgresRetirementLedger(db.DataSource);
        var subject = new SoulRetirementSubject(Guid.NewGuid(), Guid.NewGuid());
        var ct = TestContext.Current.CancellationToken;

        await ledger.RecordAsync(subject, new(RetirementStepKind.PassportRevocation, RetirementStepStatus.Failed, "temporary"), ct);
        await ledger.RecordAsync(subject, new(RetirementStepKind.PassportRevocation, RetirementStepStatus.Done, null), ct);
        await ledger.RecordAsync(subject, new(RetirementStepKind.TokenRevocation, RetirementStepStatus.Deferred, "not due"), ct);

        var rows = await ledger.ReadAsync(subject, ct);

        Assert.Equal(
            [
                new(RetirementStepKind.PassportRevocation, RetirementStepStatus.Done, null),
                new(RetirementStepKind.TokenRevocation, RetirementStepStatus.Deferred, "not due"),
            ],
            rows);
    }

    [Fact]
    public async Task Keeps_subjects_isolated()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ledger = new PostgresRetirementLedger(db.DataSource);
        var subject = new SoulRetirementSubject(Guid.NewGuid(), Guid.NewGuid());
        var ct = TestContext.Current.CancellationToken;

        await ledger.RecordAsync(subject, new(RetirementStepKind.VmStop, RetirementStepStatus.Done, null), ct);

        Assert.Empty(await ledger.ReadAsync(new SoulRetirementSubject(subject.SoulId, Guid.NewGuid()), ct));
        Assert.Empty(await ledger.ReadAsync(new SoulRetirementSubject(Guid.NewGuid(), subject.AgentId), ct));
    }
}
