using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// <c>custody.identityKek</c> through the install-settings step (doc 45 step 5): it writes <c>identity.kek_provider</c>, a second apply
/// changes nothing, rollback removes the row, and an unregistered key fails the step. These need a test PostgreSQL; a host without one skips them.
/// </summary>
public sealed class InstallSettingsIdentityKekTests
{
    private const string _key = "identity.kek_provider";

    private static SetupPlan Plan(string? identityKek) => new() { Custody = new SetupCustodySection { IdentityKek = identityKek } };

    private static SetupApplyContext Context(string hash = "plan-hash-kek") =>
        new(hash, SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private static async Task<string?> StoredAsync(Npgsql.NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT value::text FROM setting WHERE key = 'identity.kek_provider'");
        return await command.ExecuteScalarAsync(ct) as string;
    }

    [Fact]
    public async Task Apply_writes_the_provider_and_a_second_apply_changes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new InstallSettingsStep(db.DataSource, new CatalogWithKey());

        var first = await step.ApplyAsync(Plan("tpm"), Context(), ct);
        var second = await step.ApplyAsync(Plan("tpm"), Context("plan-hash-kek-2"), ct);

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal("\"tpm\"", await StoredAsync(db.DataSource, ct));
        Assert.True(second.Succeeded, second.Message);
        Assert.StartsWith("0 of", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_field_writes_no_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await new InstallSettingsStep(db.DataSource, new CatalogWithKey()).ApplyAsync(Plan(null), Context(), ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(await StoredAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Rollback_removes_the_row_the_run_created()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var step = new InstallSettingsStep(db.DataSource, new CatalogWithKey());
        await step.ApplyAsync(Plan("openbao"), Context(), ct);

        await step.RollbackAsync(Plan("openbao"), Context(), ct);

        Assert.Null(await StoredAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task A_catalog_without_the_key_fails_the_step_naming_the_key_and_not_the_value()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        var result = await new InstallSettingsStep(db.DataSource, RegistrySetupSettingCatalog.Known).ApplyAsync(Plan("openbao"), Context(), ct);

        Assert.False(result.Succeeded);
        Assert.Contains(_key, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("openbao", result.Message, StringComparison.Ordinal);
        Assert.Null(await StoredAsync(db.DataSource, ct));
    }

    [Fact]
    public void The_step_covers_the_identity_kek_section() =>
        Assert.Contains("custody.identityKek", InstallSettingsStep.CoveredSections);

    private sealed class CatalogWithKey : ISetupSettingCatalog
    {
        private readonly KeyDefinition _definition = new();

        public IReadOnlyCollection<ISetupSettingDefinition> Definitions => [.. RegistrySetupSettingCatalog.Known.Definitions, _definition];

        public bool TryGet(string name, out ISetupSettingDefinition? definition)
        {
            if (string.Equals(name, _key, StringComparison.Ordinal))
            {
                definition = _definition;
                return true;
            }

            return RegistrySetupSettingCatalog.Known.TryGet(name, out definition);
        }
    }

    private sealed class KeyDefinition : ISetupSettingDefinition
    {
        public string Name => _key;

        public bool IsInstallScope => true;

        public Type ValueType => typeof(string);

        public string? CheckValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? null : "must be a string.";

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) => new(true, null, proposed.GetRawText());
    }
}
