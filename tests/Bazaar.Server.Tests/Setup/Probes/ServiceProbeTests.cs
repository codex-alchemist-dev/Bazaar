using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Probes;
using Npgsql;

namespace Bazaar.Server.Tests.Setup.Probes;

public sealed class ServiceProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakePostgres(Func<CancellationToken, Task<PostgresServerReading>> read) : IPostgresServerReader
    {
        public Task<PostgresServerReading> ReadAsync(CancellationToken cancellationToken) => read(cancellationToken);
    }

    [Theory]
    [InlineData(180001, true, 18, SetupProbeOutcome.Healthy)]
    [InlineData(170005, true, 18, SetupProbeOutcome.Degraded)]
    [InlineData(180001, false, 18, SetupProbeOutcome.Failed)]
    public async Task Postgres_is_judged_on_version_and_pgvector(int versionNum, bool pgvector, int planned, SetupProbeOutcome expected)
    {
        var plan = new SetupPlan { Database = new SetupDatabaseSection { PostgresMajor = planned } };
        var probe = new PostgresHealthProbe(new FakePostgres(_ => Task.FromResult(new PostgresServerReading(versionNum, pgvector))));

        var reading = await probe.ProbeAsync(ProbeTestPlans.Context(plan), Ct);

        Assert.Equal(expected, reading.Outcome);
    }

    [Fact]
    public async Task A_postgres_that_never_answers_fails_at_the_limit_instead_of_hanging()
    {
        var probe = new PostgresHealthProbe(
            new FakePostgres(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new PostgresServerReading(180000, true);
            }),
            TimeSpan.FromMilliseconds(50));

        var reading = await probe.ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.Contains("in time", reading.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_postgres_error_never_leaks_its_text()
    {
        var probe = new PostgresHealthProbe(new FakePostgres(_ => throw new InvalidOperationException("password=hunter2 host=db.internal")));

        var reading = await probe.ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.DoesNotContain("hunter2", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
        Assert.DoesNotContain("db.internal", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_real_reader_against_a_closed_port_fails_cleanly()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=hunter2;Database=x;Timeout=2;Pooling=false");
        var probe = new PostgresHealthProbe(new NpgsqlPostgresServerReader(dataSource), TimeSpan.FromSeconds(5));

        var reading = await probe.ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.DoesNotContain("hunter2", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
    }

    private sealed class FakeSchema(Func<Task<SetupSchemaState>> read) : ISchemaStateReader
    {
        public Task<SetupSchemaState> ReadAsync(CancellationToken cancellationToken) => read();
    }

    [Theory]
    [InlineData(SetupSchemaState.Current, SetupProbeOutcome.Healthy)]
    [InlineData(SetupSchemaState.Behind, SetupProbeOutcome.Failed)]
    [InlineData(SetupSchemaState.Ahead, SetupProbeOutcome.Failed)]
    public async Task The_schema_state_maps_to_an_outcome(SetupSchemaState state, SetupProbeOutcome expected)
    {
        var reading = await new SchemaMigrationsProbe(new FakeSchema(() => Task.FromResult(state))).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(expected, reading.Outcome);
    }

    [Fact]
    public async Task A_schema_read_that_throws_is_unavailable_not_green()
    {
        var reading = await new SchemaMigrationsProbe(new FakeSchema(() => throw new IOException("x"))).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
    }

    private sealed class FakeLock(bool held) : IInstanceLockReader
    {
        public bool IsHeld { get; } = held;
    }

    [Theory]
    [InlineData(true, SetupProbeOutcome.Healthy)]
    [InlineData(false, SetupProbeOutcome.Failed)]
    public async Task The_instance_lock_must_be_held_by_this_server(bool held, SetupProbeOutcome expected)
    {
        var reading = await new InstanceLockProbe(new FakeLock(held)).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(expected, reading.Outcome);
    }

    private sealed class FakeRoles(params ProviderRoleReading[] readings) : IProviderRoleReader
    {
        public IReadOnlyList<ProviderRoleReading> Read() => readings;
    }

    [Fact]
    public async Task Provider_roles_need_a_usable_cache_and_search()
    {
        var all = new FakeRoles(new("cache", 1, 1), new("search", 2, 1), new("service_host", 1, 1), new("vm", 1, 1));
        var noServiceHost = new FakeRoles(new("cache", 1, 1), new("search", 1, 1));
        var noCache = new FakeRoles(new("cache", 1, 0), new("search", 1, 1), new("service_host", 1, 1), new("vm", 1, 1));

        Assert.Equal(SetupProbeOutcome.Healthy, (await new ProviderRolesProbe(all).ProbeAsync(ProbeTestPlans.Context(), Ct)).Outcome);
        Assert.Equal(SetupProbeOutcome.Degraded, (await new ProviderRolesProbe(noServiceHost).ProbeAsync(ProbeTestPlans.Context(), Ct)).Outcome);
        var failed = await new ProviderRolesProbe(noCache).ProbeAsync(ProbeTestPlans.Context(), Ct);
        Assert.Equal(SetupProbeOutcome.Failed, failed.Outcome);
        Assert.Contains("cache", failed.Message, StringComparison.Ordinal);
    }
}
