using Bazaar.Admin.Composition;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Admin.Tests;

/// <summary>The durable apply gate (doc 45 sections 1.2 and 1.7): open only when audit_log is reachable and writable, closed otherwise.</summary>
public sealed class PostgresApplyGateTests
{
    private const string _connectionVariable = "TEST_SETUP_GATE_CONNECTION";

    /// <summary>common 0004 only (audit_log, which also creates bazaar_app), so the test needs no pgvector.</summary>
    private static Task MigrateAuditAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(new AuditOnly(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly)), MigrateCommand.CurrentProvider(), ct);
        new MigrationRunner(dataSource).MigrateAsync(new AuditOnly(new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly)), MigrateCommand.CurrentProvider(), ct);

    private static Task NoMigrationAsync(NpgsqlDataSource dataSource, CancellationToken ct) => Task.CompletedTask;

    private sealed class AuditOnly(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common];

        public IReadOnlyList<MigrationFile> Load(string ns) =>
            ns == MigrationNamespaces.Common ? [.. inner.Load(ns).Where(f => f.Version == 4)] : [];
    }

    private static Dictionary<string, string?> EnvironmentFor(string? reference, string? connection = null)
    {
        var variables = new Dictionary<string, string?>();
        if (reference is not null)
        {
            variables[PostgresApplyGate.ReferenceVariable] = reference;
        }

        if (connection is not null)
        {
            variables[_connectionVariable] = connection;
        }

        return variables;
    }

    [Fact]
    public async Task No_database_reference_leaves_the_gate_closed()
    {
        await using var gate = await PostgresApplyGate.FromEnvironmentAsync(EnvironmentFor(null), TestContext.Current.CancellationToken);

        Assert.False(gate.DurableAuditStoreAvailable);
        Assert.Throws<InvalidOperationException>(() => gate.RegisterApplyServices(new ServiceCollection()));
    }

    [Fact]
    public void The_gate_covers_the_first_run_section_like_the_host_does()
    {
        Assert.Contains("firstRun", PostgresApplyGate.Closed.CoveredSections);
        Assert.Contains("install.identity", PostgresApplyGate.Closed.CoveredSections);
        Assert.Contains("owner", PostgresApplyGate.Closed.CoveredSections);
        Assert.Contains("owner.recovery", PostgresApplyGate.Closed.CoveredSections);
        Assert.DoesNotContain("owner.backupOwners", PostgresApplyGate.Closed.CoveredSections);
        Assert.Contains("settings", PostgresApplyGate.Closed.CoveredSections);
        Assert.All(PostgresApplyGate.Closed.CoveredSections, id => Assert.True(SetupPlanSections.IsKnown(id), id));
    }

    [Theory]
    [InlineData("dpapi:C:\\secret.bin")]
    [InlineData("env:TEST_SETUP_GATE_MISSING")]
    [InlineData("file:/nonexistent/path/to/connection")]
    public async Task A_reference_the_bootstrap_resolver_cannot_read_leaves_the_gate_closed(string reference)
    {
        await using var gate = await PostgresApplyGate.FromEnvironmentAsync(EnvironmentFor(reference), TestContext.Current.CancellationToken);

        Assert.False(gate.DurableAuditStoreAvailable);
    }

    [Fact]
    public async Task An_unreachable_database_leaves_the_gate_closed()
    {
        var variables = EnvironmentFor("env:" + _connectionVariable, "Host=127.0.0.1;Port=1;Username=x;Database=x;Timeout=2;Pooling=false");

        await using var gate = await PostgresApplyGate.FromEnvironmentAsync(variables, TestContext.Current.CancellationToken);

        Assert.False(gate.DurableAuditStoreAvailable);
    }

    [Fact]
    public async Task A_database_without_audit_log_leaves_the_gate_closed()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("gate", NoMigrationAsync, ct);
        var variables = EnvironmentFor("env:" + _connectionVariable, db.ConnectionString);

        await using var gate = await PostgresApplyGate.FromEnvironmentAsync(variables, ct);

        Assert.False(gate.DurableAuditStoreAvailable);
    }

    [Fact]
    public async Task A_migrated_database_opens_the_gate_and_registers_a_working_lock_and_audit_log()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("gate", MigrateAuditAsync, ct);
        var variables = EnvironmentFor("env:" + _connectionVariable, db.ConnectionString);
        await using var gate = await PostgresApplyGate.FromEnvironmentAsync(variables, ct);
        Assert.True(gate.DurableAuditStoreAvailable);

        var services = new ServiceCollection();
        gate.RegisterApplyServices(services);
        using var provider = services.BuildServiceProvider();

        services.AddSingleton<ISetupSettingCatalog>(RegistrySetupSettingCatalog.Known);
        gate.RegisterApplyServices(services);
        using var provider = services.BuildServiceProvider();

        // The CLI registers the same steps as the host, so a CLI apply and a host apply do the same thing.
        Assert.Equal(
            ["company.profile", "owner.create", "constitution.seed", "firstrun.seed", "models.sources", "install.settings"],
            provider.GetServices<ISetupStep>().OrderBy(step => step.Order).Select(step => step.Id));

        var instanceLock = provider.GetRequiredService<ISetupInstanceLock>();
        await using (await instanceLock.AcquireAsync(ct))
        {
            await Assert.ThrowsAsync<SetupInstanceLockHeldException>(() => instanceLock.AcquireAsync(ct));
        }

        await provider.GetRequiredService<IAuditLog>().AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "setup:test", "setup.plan.applied", "{}"), ct);
        Assert.IsAssignableFrom<ISettingAuditSink>(provider.GetRequiredService<ISettingAuditSink>());
        Assert.IsAssignableFrom<ISetupSettingAudit>(provider.GetRequiredService<ISetupSettingAudit>());
        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE kind = 'setup.plan.applied'");
        Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Disposing_the_gate_releases_its_connections()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("gate", MigrateAuditAsync, ct);
        var gate = await PostgresApplyGate.FromEnvironmentAsync(EnvironmentFor("env:" + _connectionVariable, db.ConnectionString), ct);

        await gate.DisposeAsync();

        Assert.False(gate.DurableAuditStoreAvailable);
    }
}
