using System.Reflection;
using Bazaar.Server.Vms.Environments;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Vms.Tests.Environments;

/// <summary>
/// A cloned test database with the <c>vms</c> component migrations applied, plus helpers that insert parent rows.
/// This project cannot reference Bazaar.Server, so it applies the embedded component SQL itself, in file-name order.
/// </summary>
public sealed class VmDb : PgFixture, IAsyncLifetime
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("VmDb is not initialised.");

    /// <summary>The repository under test, bound to this class's database.</summary>
    public EnvironmentRepository Repository => new(DataSource);

    public override async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database; each test skips itself (see Assert.SkipWhen in the tests).
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(ConnectionString);
        await ApplyComponentMigrationsAsync();
    }

    private int _nextVersion = 100;

    /// <summary>Inserts a golden image. Without an explicit version it takes the next free one for the OS, so tests never collide.</summary>
    public async Task<GoldenImageRecord> InsertImageAsync(
        VmOs os = VmOs.Linux, int? version = null, GoldenImageState state = GoldenImageState.Ready)
    {
        var image = new GoldenImageRecord(
            Guid.CreateVersion7(), os, version ?? Interlocked.Increment(ref _nextVersion), new string('a', 64), "1.0.0", state, Epoch);
        await Repository.InsertGoldenImageAsync(image, TestContext.Current.CancellationToken);
        return image;
    }

    public async Task<EnvironmentVmRecord> InsertVmAsync(
        Guid agentId, Guid imageId, VmOs os = VmOs.Linux, VmState state = VmState.Stopped)
    {
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), agentId, os, imageId, null, state, 2, 2048, 30, Epoch, Epoch);
        await Repository.InsertVmAsync(vm, TestContext.Current.CancellationToken);
        return vm;
    }

    private async Task ApplyComponentMigrationsAsync()
    {
        var assembly = typeof(EnvironmentRepository).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.vms.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No vms migrations are embedded in Bazaar.Server.Vms.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
    }
}
