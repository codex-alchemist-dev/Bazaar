using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// The install-settings step (doc 45 sections 1.2 and 1.3): what it writes, that a second apply changes nothing, that rollback restores
/// what was there, and that no value reaches a message. The registry is the server's real one, as the host and the admin tool use it.
/// </summary>
public sealed class InstallSettingsStepTests
{
    private const string _canary = "canary-secret-0000";
    private const string _operator = "operator-test";

    private static SetupPlan Plan(string channel = "beta", int port = 8443, string bind = "loopback", bool scam = false, int shutdown = 20) =>
        new()
        {
            Install = new SetupInstallSection { ReleaseChannel = channel },
            Network = new SetupNetworkSection { StatusBind = bind, StatusPort = port },
            ScamMailProtocol = new SetupScamMailSection { Enabled = scam },
            Settings = { ["host.shutdown_timeout_seconds"] = JsonSerializer.SerializeToElement(shutdown) },
        };

    private static SetupApplyContext Context(string hash = "plan-hash-1") =>
        new(hash, SetupEntryPath.Cli, _operator, null, null, SetupStateSnapshot.Empty, []);

    private static InstallSettingsStep Step(NpgsqlDataSource dataSource) => new(dataSource, RegistrySetupSettingCatalog.Known);

    private static async Task<Dictionary<string, string>> RowsAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand("SELECT key, value::text FROM setting ORDER BY key");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    [Fact]
    public async Task Apply_writes_the_settings_map_and_the_mapped_plan_fields()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Step(db.DataSource).ApplyAsync(Plan(channel: "latest-commit", port: 9000, scam: false), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal(4, rows.Count);
        Assert.Equal(12, rows.Count);
        Assert.Equal("20", rows["host.shutdown_timeout_seconds"]);
        Assert.Equal("\"loopback\"", rows["host.status.bind"]);
        Assert.Equal("9000", rows["host.status.port"]);
        Assert.Equal("\"latest_commit\"", rows["update.channel"]);
        await using var by = db.DataSource.CreateCommand("SELECT DISTINCT updated_by FROM setting");
        Assert.Equal(_operator, await by.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData("automatic", "true")]
    [InlineData("manual", "false")]
    public async Task Apply_writes_the_security_update_policy_as_the_auto_patch_flag(string security, string stored)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var plan = new SetupPlan { Install = new SetupInstallSection { UpdatePolicy = new SetupUpdatePolicySection { Security = security } } };

        var result = await Step(db.DataSource).ApplyAsync(plan, Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(stored, (await RowsAsync(db.DataSource, ct))["deps.update.autoSecurityPatches"]);
    }

    [Fact]
    public async Task Applying_twice_is_one_change()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);

        var first = await step.ApplyAsync(Plan(), Context(), ct);
        await using var stamp = db.DataSource.CreateCommand("SELECT max(updated_at) FROM setting");
        var written = (DateTime)(await stamp.ExecuteScalarAsync(ct))!;
        var second = await step.ApplyAsync(Plan(), Context(), ct);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("4 of 4 settings changed.", first.Message);
        Assert.Equal("0 of 4 settings changed.", second.Message);
        await using var after = db.DataSource.CreateCommand("SELECT max(updated_at) FROM setting");
        Assert.Equal(written, (DateTime)(await after.ExecuteScalarAsync(ct))!);
        Assert.Equal(4, (await RowsAsync(db.DataSource, ct)).Count);
        Assert.Equal("12 of 12 settings changed.", first.Message);
        Assert.Equal("0 of 12 settings changed.", second.Message);
        await using var after = db.DataSource.CreateCommand("SELECT max(updated_at) FROM setting");
        Assert.Equal(written, (DateTime)(await after.ExecuteScalarAsync(ct))!);
        Assert.Equal(12, (await RowsAsync(db.DataSource, ct)).Count);
    }

    [Fact]
    public async Task A_changed_plan_updates_only_the_changed_keys()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Plan(), Context("hash-a"), ct);

        var result = await step.ApplyAsync(Plan(port: 9001), Context("hash-b"), ct);

        Assert.Equal("1 of 4 settings changed.", result.Message);
        Assert.Equal("1 of 12 settings changed.", result.Message);
        Assert.Equal("9001", (await RowsAsync(db.DataSource, ct))["host.status.port"]);
    }

    [Fact]
    public async Task Rollback_removes_the_rows_the_run_created()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Plan(), Context(), ct);

        await step.RollbackAsync(Plan(), Context(), ct);

        Assert.Empty(await RowsAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Rollback_restores_the_value_and_the_writer_that_were_there_before()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Plan(port: 9000), new SetupApplyContext("hash-a", SetupEntryPath.Cli, "first-operator", null, null, SetupStateSnapshot.Empty, []), ct);
        await step.ApplyAsync(Plan(port: 9001), Context("hash-b"), ct);

        await step.RollbackAsync(Plan(port: 9001), Context("hash-b"), ct);

        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("9000", rows["host.status.port"]);
        Assert.Equal(4, rows.Count);
        Assert.Equal(12, rows.Count);
        await using var by = db.DataSource.CreateCommand("SELECT updated_by FROM setting WHERE key = 'host.status.port'");
        Assert.Equal("first-operator", await by.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Rollback_leaves_a_value_that_someone_else_changed_after_the_run()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Plan(), Context(), ct);
        await using (var edit = db.DataSource.CreateCommand("UPDATE setting SET value = '9999'::jsonb WHERE key = 'host.status.port'"))
        {
            await edit.ExecuteNonQueryAsync(ct);
        }

        await step.RollbackAsync(Plan(), Context(), ct);

        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("9999", rows["host.status.port"]);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Rollback_of_a_run_that_changed_nothing_does_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Plan(), Context("hash-a"), ct);
        await step.ApplyAsync(Plan(), Context("hash-b"), ct);

        await step.RollbackAsync(Plan(), Context("hash-b"), ct);

        Assert.Equal(4, (await RowsAsync(db.DataSource, ct)).Count);
        Assert.Equal(12, (await RowsAsync(db.DataSource, ct)).Count);
    }

    [Fact]
    public async Task A_secret_value_in_the_plan_reaches_neither_the_rows_nor_the_message()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var plan = Plan();

        // The plan holds references only. A reference text sits in a section the step does not write, and must stay out of everything.
        var withSecrets = new SetupPlan
        {
            Install = plan.Install,
            Network = plan.Network,
            ScamMailProtocol = plan.ScamMailProtocol,
            Settings = plan.Settings,
            Custody = new SetupCustodySection { KekRef = "env:" + _canary },
        };
        var result = await Step(db.DataSource).ApplyAsync(withSecrets, Context(), ct);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(_canary, result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, string.Join('|', (await RowsAsync(db.DataSource, ct)).Values), StringComparison.Ordinal);
    }
}
