using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Souls.Retirement;

public sealed class PostgresRetirementLedgerTests(RetirementDb db) : IClassFixture<RetirementDb>
{
    [Fact]
    public void Registration_uses_the_durable_ledger_and_allows_overrides()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddBazaarRetirementLedger();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresRetirementLedger>(provider.GetRequiredService<IRetirementLedger>());

        var overrideLedger = new InMemoryRetirementLedger();
        services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton<IRetirementLedger>(overrideLedger);
        services.AddBazaarRetirementLedger();
        using var overrideProvider = services.BuildServiceProvider();

        Assert.Same(overrideLedger, overrideProvider.GetRequiredService<IRetirementLedger>());
    }

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
