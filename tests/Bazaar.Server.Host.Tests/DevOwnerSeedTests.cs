using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Composition;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>A host environment with a fixed name, for the seed guard tests.</summary>
internal sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "Bazaar.Server.Host.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>The remote component as a Bazaar Component, and the dev-only owner seed (remote schema <c>remote/0001</c>).</summary>
public sealed class RemoteComponentTests
{
    [Fact]
    public void The_remote_component_is_in_the_host_list_and_owns_the_remote_namespace()
    {
        var component = HostComponents.All.Single(c => c is RemoteComponent);

        Assert.Equal("remote", component.Manifest.Id);
        Assert.Equal("remote", component.Manifest.MigrationNamespace);
    }
}

/// <summary>The guard and the registration of the dev-only owner seed. None of these tests touch a database.</summary>
public sealed class DevOwnerSeedGuardTests
{
    private static IConfiguration Config(bool? devEnabled)
    {
        var values = new Dictionary<string, string?>();
        if (devEnabled is { } enabled) values[RemoteAuthCompositionExtensions.DevIssuerEnabledKey] = enabled ? "true" : "false";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static FakeEnvironment Env(string name) => new(name);

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Development", null, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    public void The_seed_runs_only_when_the_flag_is_on_and_the_environment_is_development(string environment, bool? devEnabled, bool expected)
    {
        Assert.Equal(expected, DevOwnerSeed.ShouldRun(Env(environment), Config(devEnabled)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_seed_is_registered_only_when_the_dev_flag_is_on(bool devEnabled)
    {
        var services = new ServiceCollection();
        services.AddBazaarRemoteAuth(Config(devEnabled));

        var registered = services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DevOwnerSeed));

        Assert.Equal(devEnabled, registered);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task A_skipped_seed_never_touches_the_database(string environment, bool devEnabled)
    {
        // The data source points at a closed port: any attempt to seed would fail, so a clean return proves the guard held.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var seed = new DevOwnerSeed(dataSource, Env(environment), Config(devEnabled), NullLogger<DevOwnerSeed>.Instance);

        await seed.StartAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Applies the remote schema to a clean database for the seed tests. Does nothing unless <c>BAZAAR_TEST_PG</c> is set.</summary>
public sealed class DevOwnerSeedDb : PgFixture, IAsyncLifetime
{
    private const string _schemaResourceSuffix = "remote.0001_pairing_auth.sql";

    /// <summary>True when a test PostgreSQL was provided through <c>BAZAAR_TEST_PG</c>.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public override async ValueTask InitializeAsync()
    {
        if (!Enabled)
            return;
        await base.InitializeAsync();
        await using var cmd = DataSource.CreateCommand(ReadSchema());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ReadSchema()
    {
        var assembly = typeof(RemoteComponent).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(_schemaResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The remote auth schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>The dev owner insert is idempotent on a real PostgreSQL.</summary>
public sealed class DevOwnerSeedTests(DevOwnerSeedDb db) : IClassFixture<DevOwnerSeedDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Running_the_seed_twice_leaves_one_owner_row()
    {
        Assert.SkipUnless(DevOwnerSeedDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL.");
        var seed = new DevOwnerSeed(db.DataSource, Env(), Config(), NullLogger<DevOwnerSeed>.Instance);

        await seed.SeedAsync(Ct);
        await seed.SeedAsync(Ct);

        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM human WHERE id = $1 AND role = 'owner'");
        count.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = DevOwnerSeed.OwnerId });
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    private static FakeEnvironment Env() => new("Development");

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [RemoteAuthCompositionExtensions.DevIssuerEnabledKey] = "true",
    }).Build();
}
