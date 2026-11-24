using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// The backups and email sections written through the install-settings step (doc 45 steps 8 and 11; doc 33; doc 22): the rows, the
/// acknowledgement rule, idempotence and rollback. These need a test PostgreSQL, so they skip where none is available.
/// </summary>
public sealed class InstallSettingsStoresTests
{
    private const string _operator = "operator-test";

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

    private static SetupPlan Backups(string? target, int days = 30, bool acknowledge = false) =>
        new() { Backups = new SetupBackupsSection { Target = target, DrillIntervalDays = days, AcknowledgeNoTarget = acknowledge } };

    [Fact]
    public async Task A_local_target_is_stored_with_its_drill_interval_and_no_acknowledgement()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Step(db.DataSource).ApplyAsync(Backups("file:/mnt/backup/bazaar", days: 14), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("\"file:/mnt/backup/bazaar\"", rows["backup.target"]);
        Assert.Equal("14", rows["backup.drill_interval_days"]);
        Assert.Equal("false", rows["backup.no_target_acknowledged"]);
    }

    [Fact]
    public async Task No_target_with_the_acknowledgement_stores_an_empty_target_and_the_acknowledgement()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Step(db.DataSource).ApplyAsync(Backups(null, acknowledge: true), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("\"\"", rows["backup.target"]);
        Assert.Equal("true", rows["backup.no_target_acknowledged"]);
    }

    [Fact]
    public async Task A_later_apply_with_a_target_clears_the_acknowledgement()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(Backups(null, acknowledge: true), Context("hash-a"), ct);

        await step.ApplyAsync(Backups("file:/mnt/backup/bazaar", acknowledge: true), Context("hash-b"), ct);

        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("\"file:/mnt/backup/bazaar\"", rows["backup.target"]);
        Assert.Equal("false", rows["backup.no_target_acknowledged"]);
    }

    [Fact]
    public async Task An_enabled_email_stores_the_flag_and_the_back_end()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var plan = new SetupPlan { Email = new SetupEmailSection { Enabled = true, Backend = "cloudflare" } };

        var result = await Step(db.DataSource).ApplyAsync(plan, Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("true", rows["mail.enabled"]);
        Assert.Equal("\"cloudflare\"", rows["mail.backend"]);
    }

    [Fact]
    public async Task A_disabled_email_stores_off_and_no_back_end()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Step(db.DataSource).ApplyAsync(new SetupPlan(), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Equal("false", rows["mail.enabled"]);
        Assert.Equal("\"none\"", rows["mail.backend"]);
    }

    [Fact]
    public async Task Rollback_restores_the_previous_email_choice()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        await step.ApplyAsync(new SetupPlan { Email = new SetupEmailSection { Enabled = true, Backend = "imap_smtp" } }, Context("hash-a"), ct);
        var changed = new SetupPlan { Email = new SetupEmailSection { Enabled = true, Backend = "cloudflare" } };
        await step.ApplyAsync(changed, Context("hash-b"), ct);

        await step.RollbackAsync(changed, Context("hash-b"), ct);

        Assert.Equal("\"imap_smtp\"", (await RowsAsync(db.DataSource, ct))["mail.backend"]);
    }

    [Fact]
    public async Task Applying_the_same_backups_and_email_twice_changes_nothing_the_second_time()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = Step(db.DataSource);
        var plan = new SetupPlan
        {
            Backups = new SetupBackupsSection { Target = "file:/mnt/backup/bazaar" },
            Email = new SetupEmailSection { Enabled = true, Backend = "bazaar_client" },
        };

        await step.ApplyAsync(plan, Context("hash-a"), ct);
        var second = await step.ApplyAsync(plan, Context("hash-b"), ct);

        Assert.True(second.Succeeded);
        // The projection also maps fields owned by other steps' sections, so the total moves when one is added; the count changed must stay 0.
        Assert.Matches(@"^0 of \d+ settings changed\.$", second.Message);
    }
}
