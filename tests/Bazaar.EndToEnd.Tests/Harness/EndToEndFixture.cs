using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// One running Bazaar for the whole class: an empty Postgres database migrated by the host's own <c>migrate</c> command, a keyless
/// stub model, one seeded group chat with the owner and one agent, and the real server process on a loopback port.
/// </summary>
public sealed class EndToEndFixture : IAsyncLifetime
{
    public const string DevToken = "e2e-dev-token-0123456789abcdef";
    public const string FreeLlmKey = "freellmapi-e2e-0123456789abcdef";

    public static readonly Guid OwnerId = new("0e2e0000-0000-4000-8000-000000000001");
    public static readonly Guid AgentId = new("0e2e0000-0000-4000-8000-000000000002");
    public static readonly Guid GcId = new("0e2e0000-0000-4000-8000-000000000003");

    private string _database = "";
    private string _admin = "";
    private string _dataRoot = "";

    public StubModelServer Model { get; private set; } = null!;

    public HostProcess Host { get; private set; } = null!;

    public NpgsqlDataSource Db { get; private set; } = null!;

    /// <summary>True when nothing was started because this host has no test PostgreSQL (Windows without BAZAAR_TEST_PG).</summary>
    public bool Unsupported { get; private set; }

    /// <summary>Skips the calling test when <see cref="Unsupported"/>.</summary>
    public void RequireRunning() => Assert.SkipWhen(Unsupported, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    public async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            Unsupported = true;
            return;
        }

        var source = await PgSource.GetSharedAsync();
        _admin = source.AdminConnectionString;
        _database = "e2e_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\" TEMPLATE template0", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = source.ConnectionStringFor(_database);
        _dataRoot = Path.Combine(Path.GetTempPath(), "bazaar-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        Model = await StubModelServer.StartAsync();

        var port = HostProcess.FreePort();
        var env = new Dictionary<string, string>
        {
            ["BAZAAR_E2E_DB"] = connectionString,
            ["BAZAAR_E2E_DEV_TOKEN"] = DevToken,
            ["BAZAAR_E2E_FREELLM_KEY"] = FreeLlmKey,
        };
        string[] keys =
        [
            $"--Bazaar:Host:DataRoot={_dataRoot}",
            "--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_E2E_DB",
            $"--Bazaar:Host:Status:Port={port}",
            "--Bazaar:DevAuth:Enabled=true",
            // Stand-in (GAPS.md): the harness agent has no Soul, so posts are allowed unbound.
            "--Bazaar:Gc:AgentPost:RequireSoulBinding=false",
            "--Bazaar:DevAuth:TokenSecret=env:BAZAAR_E2E_DEV_TOKEN",
            $"--Bazaar:DevAuth:PrincipalId={OwnerId}",
            "--Bazaar:Gateway:FreeLlmApi:Enabled=true",
            $"--Bazaar:Gateway:FreeLlmApi:BaseUrl={Model.BaseUrl}",
            "--Bazaar:Gateway:FreeLlmApi:ApiKey=env:BAZAAR_E2E_FREELLM_KEY",
            $"--Bazaar:Gateway:FreeLlmApi:DefaultModel={StubModelServer.Model}",
            $"--Bazaar:AgentResponder:Agents:0:AgentId={AgentId}",
            "--Bazaar:AgentResponder:Agents:0:Name=Smoke",
            "--Bazaar:AgentResponder:Agents:0:SystemPrompt=You are the smoke-test agent.",
        ];

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await HostProcess.MigrateAsync(keys, env, cts.Token);

        Db = NpgsqlDataSource.Create(connectionString);
        await SeedCompanyAsync(cts.Token);

        Host = await HostProcess.StartAsync(port, keys, env, TimeSpan.FromSeconds(90), cts.Token);
    }

    /// <summary>
    /// Stand-in for "create a company": today no wire request, HTTP route or setup step creates a group chat or an agent, so the
    /// harness writes the one GC and its two members straight into the schema. See GAPS.md, gap 2.
    /// </summary>
    private async Task SeedCompanyAsync(CancellationToken ct)
    {
        await using var batch = Db.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'Smoke test', $2)")
        {
            Parameters = { new() { Value = GcId }, new() { Value = OwnerId } },
        });
        foreach (var member in new[] { OwnerId, AgentId })
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES ($1, $2, 'member', 0)")
            {
                Parameters = { new() { Value = GcId }, new() { Value = member } },
            });
        }

        await batch.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Unsupported) return;
        if (Host is not null) await Host.DisposeAsync();
        if (Model is not null) await Model.DisposeAsync();
        if (Db is not null) await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var admin = new NpgsqlConnection(_admin);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
            // The shared test server is torn down at process exit anyway.
        }

        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }
}
