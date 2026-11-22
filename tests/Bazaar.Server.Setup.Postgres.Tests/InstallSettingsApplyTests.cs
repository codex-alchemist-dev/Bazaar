using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// The applier with the real install-settings step over a real database: an apply writes the settings and the audit rows, a second apply
/// of the same plan changes no setting, and a later failing step rolls the settings back. No secret text reaches the audit rows.
/// </summary>
public sealed class InstallSettingsApplyTests
{
    private const string _canary = "canary-secret-0000";

    private const string _plan = """
        {
          "schemaVersion": 1,
          "install": {
            "companyName": "Example Studio",
            "companySlug": "example-studio",
            "locale": "en",
            "timezone": "Europe/London",
            "releaseChannel": "beta",
            "updatePolicy": { "security": "automatic", "other": "manual" }
          },
          "database": { "postgresMajor": 18, "dataRoot": "C:\\ProgramData\\Bazaar", "gcPartitionCount": 16 },
          "custody": {
            "kekProvider": "dpapi",
            "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            "auditAnchorTarget": "https://anchor.example.invalid/bazaar-audit",
            "mergeCredential": { "kind": "owner_app", "appIdRef": "secret://bazaar/owner/app-id" }
          },
          "owner": {
            "displayName": "Owner",
            "requireSecondFactor": true,
            "recovery": { "printedCodes": false, "backupOwners": [] }
          },
          "privacy": { "profile": "private" },
          "backups": { "target": "file:/mnt/backup/bazaar", "drillIntervalDays": 30, "acknowledgeNoTarget": false },
          "models": {
            "sources": [
              { "id": "freellmapi", "keyRef": "secret://bazaar/providers/canary-secret-0000/key" },
              { "id": "anthropic-owner", "keyRef": "secret://bazaar/providers/anthropic/key" }
            ],
            "defaultPreset": "free-lane"
          },
          "email": { "enabled": false },
          "scamMailProtocol": { "enabled": false },
          "company": { "source": "template:starter@1.0.0" },
          "modules": ["Bazaar.GC", "Bazaar.Constellation", "Bazaar.KODO"],
          "network": { "statusBind": "loopback", "statusPort": 8443 },
          "settings": { "host.shutdown_timeout_seconds": 20 }
        }
        """;

    private static SetupPlan Plan() => SetupPlanLoader.Load(_plan, RegistrySetupSettingCatalog.Known).Plan!;

    private static SetupApplier Applier(NpgsqlDataSource dataSource, params ISetupStep[] extra)
    {
        var audit = new PostgresAuditLog(dataSource);
        return new SetupApplier(
            new PostgresSetupInstanceLock(dataSource),
            [new CoverTheRestStep(), new InstallSettingsStep(dataSource, RegistrySetupSettingCatalog.Known), .. extra],
            audit,
            new SettingAuditSinkSetupAudit(new PostgresSettingAuditSink(audit)),
            TimeProvider.System,
            RegistrySetupSettingCatalog.Known);
    }

    private static SetupApplyRequest Request(SetupPlan plan) =>
        new(plan, SetupPlanCanonicalizer.Hash(plan), SetupEntryPath.Cli, "operator-test");

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task An_apply_writes_the_settings_and_one_audit_row_per_change_and_leaks_no_reference_text()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Applier(db.DataSource).ApplyAsync(Request(Plan()), ct);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(["test.cover_the_rest", "install.settings"], result.AppliedStepIds);
        Assert.Equal(5, await CountAsync(db.DataSource, "SELECT count(*) FROM setting", ct));
        Assert.Equal(1, await CountAsync(db.DataSource, "SELECT count(*) FROM audit_log WHERE kind = 'setup.plan.applied'", ct));
        Assert.Equal(5, await CountAsync(db.DataSource, "SELECT count(*) FROM audit_log WHERE kind = 'setting.changed'", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, $"SELECT count(*) FROM audit_log WHERE payload::text LIKE '%{_canary}%'", ct));
    }

    [Fact]
    public void The_step_covers_only_registered_sections()
    {
        Assert.All(InstallSettingsStep.CoveredSections, id => Assert.True(SetupPlanSections.IsKnown(id), id));
        Assert.Equal(InstallSettingsStep.CoveredSections.Order(StringComparer.Ordinal), new InstallSettingsStep(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none"), RegistrySetupSettingCatalog.Known).Covers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_real_step_alone_holds_a_full_plan_and_writes_no_setting()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var audit = new PostgresAuditLog(db.DataSource);
        var applier = new SetupApplier(
            new PostgresSetupInstanceLock(db.DataSource),
            [new InstallSettingsStep(db.DataSource, RegistrySetupSettingCatalog.Known)],
            audit,
            new SettingAuditSinkSetupAudit(new PostgresSettingAuditSink(audit)),
            TimeProvider.System,
            RegistrySetupSettingCatalog.Known);

        var result = await applier.ApplyAsync(Request(Plan()), ct);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Empty(result.AppliedStepIds);
        Assert.Contains("owner", result.UnappliedSections);
        Assert.DoesNotContain("settings", result.UnappliedSections);
        Assert.Equal(0, await CountAsync(db.DataSource, "SELECT count(*) FROM setting", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "SELECT count(*) FROM audit_log WHERE kind = 'setting.changed'", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, $"SELECT count(*) FROM audit_log WHERE payload::text LIKE '%{_canary}%'", ct));
        await using var command = db.DataSource.CreateCommand("SELECT payload::text FROM audit_log WHERE kind = 'setup.plan.applied'");
        var payload = (JsonObject)JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!;
        Assert.Equal("held_unapplied_sections", payload["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Applying_the_same_plan_twice_leaves_the_settings_as_they_were()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var applier = Applier(db.DataSource);

        await applier.ApplyAsync(Request(Plan()), ct);
        var stamp = await CountAsync(db.DataSource, "SELECT extract(epoch FROM max(updated_at))::bigint * 1000000 + extract(microseconds FROM max(updated_at))::bigint % 1000000 FROM setting", ct);
        var second = await applier.ApplyAsync(Request(Plan()), ct);

        Assert.Equal(SetupExitCode.Ok, second.ExitCode);
        Assert.Equal(5, await CountAsync(db.DataSource, "SELECT count(*) FROM setting", ct));
        Assert.Equal(stamp, await CountAsync(db.DataSource, "SELECT extract(epoch FROM max(updated_at))::bigint * 1000000 + extract(microseconds FROM max(updated_at))::bigint % 1000000 FROM setting", ct));
    }

    [Fact]
    public async Task A_later_failing_step_rolls_the_settings_back_and_the_failure_row_holds_no_value()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await Applier(db.DataSource, new ThrowingStep()).ApplyAsync(Request(Plan()), ct);

        Assert.Equal(SetupExitCode.ApplyFailedRolledBack, result.ExitCode);
        Assert.Equal(0, await CountAsync(db.DataSource, "SELECT count(*) FROM setting", ct));
        await using var command = db.DataSource.CreateCommand("SELECT payload::text FROM audit_log WHERE kind = 'setup.plan.applied'");
        var payload = (JsonObject)JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!;
        Assert.Equal("rolled_back", payload["outcome"]!.GetValue<string>());
        Assert.Equal("later.step", payload["failedStep"]!.GetValue<string>());
        Assert.DoesNotContain(_canary, payload.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Stands in for the steps that are not built yet: it applies nothing and declares every section the real step does not cover,
    /// so the full test plan passes the applier's unapplied-sections check.
    /// </summary>
    private sealed class CoverTheRestStep : ISetupStep
    {
        public string Id => "test.cover_the_rest";

        public int Order => 1;

        public IReadOnlyCollection<string> Covers =>
            [.. SetupPlanSections.All.Select(section => section.Id).Except(InstallSettingsStep.CoveredSections)];

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }

    /// <summary>Runs after the settings step (order 1600) and fails with a canary in the exception text.</summary>
    private sealed class ThrowingStep : ISetupStep
    {
        public string Id => "later.step";

        public int Order => 1600;

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(_canary);
    }
}
