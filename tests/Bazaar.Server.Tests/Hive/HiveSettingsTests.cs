using System.Text.Json;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Hive.Settings;
using Bazaar.Server.Settings;
using Bazaar.Server.Tests.Settings;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Hive;

/// <summary>
/// <c>hive.grants</c> and <c>hive.allowed_kinds</c> (doc 08 settings table) against the real settings store on PostgreSQL:
/// defaults, grant read-through, the allowed-kinds filter and the write rules.
/// </summary>
public sealed class HiveSettingsTests(SettingsDb db) : IClassFixture<SettingsDb>
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);
    private static readonly SettingActor _agent = new("agent:charles", SettingRole.Agent);
    private static readonly LessonRef _lesson = new("atlas", "agents/a/memory/procedural/retry.md", HiveKind.Procedure);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(NpgsqlSettingStore Store, InMemorySettingAuditSink Audit, NpgsqlDataSource Data)> FreshAsync(SettingsDb db)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var cmd = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        await new MigrationRunner(db.DataSource).MigrateAsync(new EmbeddedMigrationSource(typeof(NpgsqlSettingStore).Assembly), "linux", Ct);
        var audit = new InMemorySettingAuditSink();
        var registry = BuiltInSettingRegistries.WithContributions([new HiveSettingContribution()]);
        return (new NpgsqlSettingStore(db.DataSource, audit, registry), audit, db.DataSource);
    }

    private static HiveGrantPolicy PolicyOver(NpgsqlSettingStore store) =>
        new(new SettingsHiveGrantStore(store), kinds: new SettingsHiveKindSource(store));

    [Fact]
    public async Task Defaults_are_no_grants_and_procedure_and_failure()
    {
        var (store, _, _) = await FreshAsync(db);
        var grants = new SettingsHiveGrantStore(store);

        Assert.Empty(await store.GetAsync(HiveSettings.Grants, cancellationToken: Ct));
        Assert.Empty(await grants.ListAsync(Ct));
        Assert.False(await grants.IsGrantedAsync("atlas", "borealis", HiveKind.Procedure, Ct));
        Assert.Equal(new HashSet<HiveKind> { HiveKind.Procedure, HiveKind.Failure }, await new SettingsHiveKindSource(store).AllowedKindsAsync(Ct));
        Assert.False(await PolicyOver(store).MayCopyAsync(_lesson, "borealis", Ct));
    }

    [Fact]
    public void Both_keys_are_install_scope_operator_settings_in_the_contribution()
    {
        var names = new HiveSettingContribution().Settings.Select(s => s.Name).ToList();

        Assert.Equal(["hive.grants", "hive.allowed_kinds"], names);
        Assert.All(new HiveSettingContribution().Settings, s =>
        {
            Assert.Equal(SettingScope.Install, s.Scope);
            Assert.Equal(SettingRole.Operator, s.WhoMayChange);
        });
    }

    [Fact]
    public async Task A_grant_written_to_the_setting_is_read_through_and_covers_only_its_pair_and_kind()
    {
        var (store, audit, _) = await FreshAsync(db);
        var result = await store.SetAsync(
            HiveSettings.Grants, [new HiveGrantEntry("atlas", "borealis", "procedure")], _operator, cancellationToken: Ct);
        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);

        var grants = new SettingsHiveGrantStore(store);
        Assert.True(await grants.IsGrantedAsync("atlas", "borealis", HiveKind.Procedure, Ct));
        Assert.False(await grants.IsGrantedAsync("borealis", "atlas", HiveKind.Procedure, Ct));
        Assert.False(await grants.IsGrantedAsync("atlas", "borealis", HiveKind.Failure, Ct));
        Assert.False(await grants.IsGrantedAsync("atlas", "cedar", HiveKind.Procedure, Ct));
        Assert.Equal([new HiveGrant("atlas", "borealis", HiveKind.Procedure, "settings")], await grants.ListAsync(Ct));
        Assert.True(await PolicyOver(store).MayCopyAsync(_lesson, "borealis", Ct));
        Assert.Contains(audit.Entries, e => e.Key == "hive.grants" && e.Outcome == SettingWriteOutcome.Accepted);
    }

    [Fact]
    public async Task Adding_and_removing_a_grant_goes_through_the_audited_setting()
    {
        var (store, audit, data) = await FreshAsync(db);
        var grants = new SettingsHiveGrantStore(store);

        await grants.AddAsync(new HiveGrant("atlas", "borealis", HiveKind.Failure, "owner"), Ct);
        await grants.AddAsync(new HiveGrant("atlas", "borealis", HiveKind.Failure, "owner"), Ct);

        // A second store instance over the same database sees it: the value lives in the setting row, not in the object.
        Assert.True(await new SettingsHiveGrantStore(store).IsGrantedAsync("atlas", "borealis", HiveKind.Failure, Ct));
        await using (var cmd = data.CreateCommand("SELECT value::text FROM setting WHERE key = 'hive.grants'"))
        {
            using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(Ct))!);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("failure", doc.RootElement[0].GetProperty("kind").GetString());
        }

        Assert.True(await grants.RemoveAsync("atlas", "borealis", HiveKind.Failure, Ct));
        Assert.False(await grants.RemoveAsync("atlas", "borealis", HiveKind.Failure, Ct));
        Assert.Empty(await grants.ListAsync(Ct));
        Assert.True(audit.Entries.Count(e => e.Key == "hive.grants" && e.Outcome == SettingWriteOutcome.Accepted) >= 2);
        await Assert.ThrowsAsync<ArgumentException>(
            () => grants.AddAsync(new HiveGrant("atlas", "atlas", HiveKind.Failure, "owner"), Ct));
    }

    [Fact]
    public async Task The_allowed_kinds_setting_filters_a_granted_kind()
    {
        var (store, _, _) = await FreshAsync(db);
        var grants = new SettingsHiveGrantStore(store);
        await grants.AddAsync(new HiveGrant("atlas", "borealis", HiveKind.Procedure, "owner"), Ct);
        var policy = PolicyOver(store);
        Assert.True(await policy.MayCopyAsync(_lesson, "borealis", Ct));

        var narrowed = await store.SetAsync(HiveSettings.AllowedKinds, ["failure"], _operator, cancellationToken: Ct);

        Assert.Equal(SettingWriteOutcome.Accepted, narrowed.Outcome);
        Assert.False(await policy.MayCopyAsync(_lesson, "borealis", Ct));
        Assert.True(await policy.IsKindAllowedAsync(HiveKind.Failure, Ct));
        Assert.False(await policy.IsKindAllowedAsync(HiveKind.Procedure, Ct));
    }

    [Fact]
    public async Task Technical_facts_are_an_opt_in_that_waits_for_approval()
    {
        var (store, _, _) = await FreshAsync(db);

        var widened = await store.SetAsync(
            HiveSettings.AllowedKinds, ["procedure", "failure", "technical-fact"], _operator, cancellationToken: Ct);

        Assert.Equal(SettingWriteOutcome.PendingApproval, widened.Outcome);
        Assert.DoesNotContain(HiveKind.TechnicalFact, await new SettingsHiveKindSource(store).AllowedKindsAsync(Ct));
    }

    [Theory]
    [InlineData("hive.allowed_kinds", """["preference"]""")]
    [InlineData("hive.allowed_kinds", """["procedure","procedure"]""")]
    [InlineData("hive.grants", """[{"from_project":"a","to_project":"a","kind":"procedure"}]""")]
    [InlineData("hive.grants", """[{"from_project":"a","to_project":"b","kind":"reputation"}]""")]
    [InlineData("hive.grants", """[{"from_project":"","to_project":"b","kind":"failure"}]""")]
    public async Task Values_that_name_an_unknown_kind_or_a_bad_pair_are_rejected(string key, string json)
    {
        var (store, _, _) = await FreshAsync(db);
        using var doc = JsonDocument.Parse(json);

        var result = await store.SetJsonAsync(key, doc.RootElement.Clone(), _operator, cancellationToken: Ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Empty(await store.GetAsync(HiveSettings.Grants, cancellationToken: Ct));
    }

    [Fact]
    public async Task Only_an_operator_may_change_the_hive_settings()
    {
        var (store, _, _) = await FreshAsync(db);

        var result = await store.SetAsync(
            HiveSettings.Grants, [new HiveGrantEntry("atlas", "borealis", "procedure")], _agent, cancellationToken: Ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Empty(await new SettingsHiveGrantStore(store).ListAsync(Ct));
    }

    [Fact]
    public async Task A_grant_the_store_does_not_accept_throws_instead_of_pretending()
    {
        var (store, _, _) = await FreshAsync(db);
        // A registry without the hive keys makes every write of them a rejection.
        var bare = new NpgsqlSettingStore(db.DataSource, new InMemorySettingAuditSink(), BuiltInSettingRegistries.Known);

        await Assert.ThrowsAsync<ArgumentException>(
            () => new SettingsHiveGrantStore(bare).AddAsync(new HiveGrant("atlas", "borealis", HiveKind.Failure, "owner"), Ct));
        Assert.Empty(await new SettingsHiveGrantStore(store).ListAsync(Ct));
    }
}
