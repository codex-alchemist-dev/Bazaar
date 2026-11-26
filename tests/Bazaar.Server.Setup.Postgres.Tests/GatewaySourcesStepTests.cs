using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// The gateway sources step over a real database (<c>gw_provider</c>, common migration 0002): what it writes, that a second apply
/// changes nothing, that operator-owned columns survive, and that rollback undoes only this run.
/// </summary>
public sealed class GatewaySourcesStepTests
{
    private const string _skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static SetupPlan Plan(string freeKey = "secret://bazaar/providers/freellmapi/key") =>
        new()
        {
            Models = new SetupModelsSection
            {
                Sources =
                [
                    new SetupModelSource { Id = "freellmapi", KeyRef = freeKey },
                    new SetupModelSource { Id = "anthropic-owner", KeyRef = "secret://bazaar/providers/anthropic/key" },
                    new SetupModelSource { Id = "ollama" },
                ],
            },
        };

    private static SetupApplyContext Context(string hash = "plan-hash-1") =>
        new(hash, SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private static async Task<List<string>> RowsAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var rows = new List<string>();
        await using var command = dataSource.CreateCommand("SELECT id || '|' || kind || '|' || adapter || '|' || base_url || '|' || auth_kind || '|' || coalesce(secret_ref, '-') FROM gw_provider ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task Apply_writes_one_provider_row_per_source_from_the_catalogue()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await new GatewaySourcesStep(db.DataSource).ApplyAsync(Plan(), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            [
                "anthropic-direct|first_party|anthropic_messages|https://api.anthropic.com|api_key|secret://bazaar/providers/anthropic/key",
                "freellmapi|gateway|openai_chat|http://127.0.0.1:3001/v1|bearer|secret://bazaar/providers/freellmapi/key",
                "ollama|local|openai_chat|http://localhost:11434/v1|none|-",
            ],
            await RowsAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Applying_twice_is_one_change()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);

        var first = await step.ApplyAsync(Plan(), Context("hash-a"), ct);
        var second = await step.ApplyAsync(Plan(), Context("hash-b"), ct);

        Assert.Equal("3 of 3 model sources changed.", first.Message);
        Assert.Equal("0 of 3 model sources changed.", second.Message);
        Assert.Equal(3, (await RowsAsync(db.DataSource, ct)).Count);
    }

    [Fact]
    public async Task A_changed_key_reference_updates_only_that_row_and_keeps_operator_owned_columns()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);
        await step.ApplyAsync(Plan(), Context("hash-a"), ct);
        await using (var edit = db.DataSource.CreateCommand("UPDATE gw_provider SET enabled = false, headers = '{\"X-Team\":\"a\"}'::jsonb, terms_ack_at = now() WHERE id = 'freellmapi'"))
        {
            await edit.ExecuteNonQueryAsync(ct);
        }

        var result = await step.ApplyAsync(Plan("secret://bazaar/providers/freellmapi/key2"), Context("hash-b"), ct);

        Assert.Equal("1 of 3 model sources changed.", result.Message);
        await using var check = db.DataSource.CreateCommand("SELECT secret_ref, enabled, headers::text, terms_ack_at IS NOT NULL FROM gw_provider WHERE id = 'freellmapi'");
        await using var reader = await check.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("secret://bazaar/providers/freellmapi/key2", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Contains("X-Team", reader.GetString(2), StringComparison.Ordinal);
        Assert.True(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Rollback_removes_the_rows_the_run_created()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);
        await step.ApplyAsync(Plan(), Context(), ct);

        await step.RollbackAsync(Plan(), Context(), ct);

        Assert.Empty(await RowsAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Rollback_restores_the_row_that_was_there_before()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);
        await step.ApplyAsync(Plan(), Context("hash-a"), ct);
        await step.ApplyAsync(Plan("secret://bazaar/providers/freellmapi/key2"), Context("hash-b"), ct);

        await step.RollbackAsync(Plan("secret://bazaar/providers/freellmapi/key2"), Context("hash-b"), ct);

        Assert.Contains("freellmapi|gateway|openai_chat|http://127.0.0.1:3001/v1|bearer|secret://bazaar/providers/freellmapi/key", await RowsAsync(db.DataSource, ct));
        Assert.Equal(3, (await RowsAsync(db.DataSource, ct)).Count);
    }

    [Fact]
    public async Task Rollback_leaves_a_row_that_someone_else_changed_after_the_run()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);
        await step.ApplyAsync(Plan(), Context(), ct);
        await using (var edit = db.DataSource.CreateCommand("UPDATE gw_provider SET base_url = 'http://127.0.0.1:9/v1' WHERE id = 'freellmapi'"))
        {
            await edit.ExecuteNonQueryAsync(ct);
        }

        await step.RollbackAsync(Plan(), Context(), ct);

        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Single(rows);
        Assert.StartsWith("freellmapi|gateway|openai_chat|http://127.0.0.1:9/v1", rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_keeps_a_created_row_that_a_model_row_now_references()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new GatewaySourcesStep(db.DataSource);
        await step.ApplyAsync(Plan(), Context(), ct);
        await using (var model = db.DataSource.CreateCommand("INSERT INTO gw_model (provider_id, model_id, family, identity, first_seen, last_seen, source) VALUES ('freellmapi', 'auto', 'auto', 'pooled', now(), now(), 'test')"))
        {
            await model.ExecuteNonQueryAsync(ct);
        }

        await step.RollbackAsync(Plan(), Context(), ct);

        var rows = await RowsAsync(db.DataSource, ct);
        Assert.Single(rows);
        Assert.StartsWith("freellmapi|", rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_reference_never_reaches_the_step_message()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await new GatewaySourcesStep(db.DataSource).ApplyAsync(Plan("secret://bazaar/providers/canary-secret-0000/key"), Context(), ct);

        Assert.DoesNotContain("canary-secret-0000", result.Message, StringComparison.Ordinal);
    }
}
