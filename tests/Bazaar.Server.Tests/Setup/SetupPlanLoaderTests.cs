using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>Parsing, schema checks, secret-reference checks, settings checks and the hash, against the file the doc 45 sketch describes.</summary>
public sealed class SetupPlanLoaderTests
{
    private static readonly string[] _sortedModules = ["Bazaar.Constellation", "Bazaar.GC", "Bazaar.KODO"];

    private static bool HasError(SetupLoadResult result, string path) =>
        result.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    /// <summary>Everything a result says, as one string, for the "no value leaks" assertions.</summary>
    private static string AllText(SetupLoadResult result) =>
        string.Join("\n", result.Issues.Select(issue => issue.Path + " " + issue.Message));

    [Fact]
    public void Valid_fixture_loads_with_no_issues_and_a_sha256_hash()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Plan);
        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.NotNull(result.PlanHash);
        Assert.Matches("^[0-9a-f]{64}$", result.PlanHash);
        Assert.Equal("Example Studio", result.Plan.Install.CompanyName);
        Assert.Equal(20, result.Plan.Settings["host.shutdown_timeout_seconds"].GetInt32());
    }

    [Fact]
    public void Hash_is_stable_across_property_order()
    {
        var original = JsonNode.Parse(SetupFixtures.Valid)!.AsObject();
        var reversed = ReverseKeys(original);

        var a = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);
        var b = SetupPlanLoader.Load(reversed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), RegistrySetupSettingCatalog.Known);

        Assert.Empty(b.Issues);
        Assert.Equal(a.PlanHash, b.PlanHash);
    }

    [Fact]
    public void Equal_plans_hash_equally_and_a_changed_value_changes_the_hash()
    {
        var first = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);
        var second = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);
        var changed = SetupPlanLoader.Load(SetupFixtures.With("\"companyName\": \"Example Studio\"", "\"companyName\": \"Other Studio\""), RegistrySetupSettingCatalog.Known);

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.NotEqual(first.PlanHash, changed.PlanHash);
    }

    [Fact]
    public void Module_order_does_not_change_the_hash_because_the_list_is_a_set()
    {
        var reordered = SetupFixtures.With(
            "[\"Bazaar.GC\", \"Bazaar.Constellation\", \"Bazaar.KODO\"]",
            "[\"Bazaar.KODO\", \"Bazaar.GC\", \"Bazaar.Constellation\", \"Bazaar.GC\"]");

        var result = SetupPlanLoader.Load(reordered, RegistrySetupSettingCatalog.Known);

        Assert.Empty(result.Issues);
        Assert.Equal(_sortedModules, result.Plan!.Modules);
        Assert.Equal(SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).PlanHash, result.PlanHash);
    }

    [Fact]
    public void Unknown_top_level_key_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"surprise\": true,"), RegistrySetupSettingCatalog.Known);

        Assert.Null(result.Plan);
        Assert.Null(result.PlanHash);
        Assert.True(HasError(result, "surprise"));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public void Unknown_nested_key_is_rejected_with_its_full_path()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"companySlug\": \"example-studio\",", "\"companySlug\": \"example-studio\", \"nickname\": \"x\","), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "install.nickname"));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public void Repeated_key_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"email\": { \"enabled\": false },", "\"email\": { \"enabled\": false }, \"email\": { \"enabled\": true },"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "email"));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_not_JSON_and_are_rejected()
    {
        var withComment = SetupFixtures.With("\"privacy\": { \"profile\": \"private\" },", "// a comment\n  \"privacy\": { \"profile\": \"private\" },");
        var withTrailingComma = SetupFixtures.With("\"email\": { \"enabled\": false },", "\"email\": { \"enabled\": false, },");

        Assert.Equal(SetupExitCode.InvalidInput, SetupPlanLoader.Load(withComment, RegistrySetupSettingCatalog.Known).ExitCode);
        Assert.Equal(SetupExitCode.InvalidInput, SetupPlanLoader.Load(withTrailingComma, RegistrySetupSettingCatalog.Known).ExitCode);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1,", "\"schemaVersion\": 2,")]
    [InlineData("\"schemaVersion\": 1,", "\"schemaVersion\": \"1\",")]
    [InlineData("\"schemaVersion\": 1,", "")]
    public void Schema_version_other_than_1_is_rejected(string oldText, string newText)
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(oldText, newText), RegistrySetupSettingCatalog.Known);

        Assert.Null(result.Plan);
        Assert.True(HasError(result, "schemaVersion"));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public void Missing_required_key_is_reported_by_path()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"company\": { \"source\": \"template:starter@1.0.0\" },", ""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "company"));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public void A_file_that_is_not_an_object_is_rejected()
    {
        Assert.Equal(SetupExitCode.InvalidInput, SetupPlanLoader.Load("[1, 2]", RegistrySetupSettingCatalog.Known).ExitCode);
        Assert.Equal(SetupExitCode.InvalidInput, SetupPlanLoader.Load("{", RegistrySetupSettingCatalog.Known).ExitCode);
    }

    [Theory]
    [InlineData("\"kekRef\": \"dpapi:C:\\\\ProgramData\\\\Bazaar\\\\kek.bin\"", "\"kekRef\": \"" + SetupFixtures.Canary + "\"", "custody.kekRef")]
    [InlineData("\"keyRef\": \"secret://bazaar/providers/freellmapi/key\"", "\"keyRef\": \"" + SetupFixtures.Canary + "\"", "models.sources[0].keyRef")]
    [InlineData("\"appIdRef\": \"secret://bazaar/owner/app-id\"", "\"appIdRef\": \"" + SetupFixtures.Canary + "\"", "custody.mergeCredential.appIdRef")]
    [InlineData("\"kekRef\": \"dpapi:C:\\\\ProgramData\\\\Bazaar\\\\kek.bin\"", "\"kekRef\": \"secret://bazaar/kek\"", "custody.kekRef")]
    public void Literal_secret_or_wrong_family_is_rejected_and_the_error_names_the_key_only(string oldFragment, string newFragment, string expectedPath)
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(oldFragment, newFragment), RegistrySetupSettingCatalog.Known);

        Assert.Null(result.Plan);
        Assert.True(HasError(result, expectedPath));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.DoesNotContain(SetupFixtures.Canary, AllText(result), StringComparison.Ordinal);
        Assert.DoesNotContain(SetupFixtures.Canary, string.Join(",", result.Issues.Select(i => i.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Kek_reference_must_use_the_family_named_by_kek_provider()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"kekProvider\": \"dpapi\"", "\"kekProvider\": \"env\""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "custody.kekRef"));
    }

    [Fact]
    public void Literal_secret_in_a_setting_value_never_reaches_the_error_text()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(
            "\"settings\": { \"host.shutdown_timeout_seconds\": 20 }",
            "\"settings\": { \"host.shutdown_timeout_seconds\": \"" + SetupFixtures.Canary + "\" }"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "settings.host.shutdown_timeout_seconds"));
        Assert.DoesNotContain(SetupFixtures.Canary, AllText(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_setting_key_and_out_of_range_setting_are_rejected()
    {
        var unknown = SetupPlanLoader.Load(SetupFixtures.With(
            "\"host.shutdown_timeout_seconds\": 20",
            "\"host.no_such_key\": 1"), RegistrySetupSettingCatalog.Known);
        var outOfRange = SetupPlanLoader.Load(SetupFixtures.With("\"host.shutdown_timeout_seconds\": 20", "\"host.shutdown_timeout_seconds\": 500"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(unknown, "settings.host.no_such_key"));
        Assert.True(HasError(outOfRange, "settings.host.shutdown_timeout_seconds"));
    }

    [Fact]
    public void Setting_outside_install_scope_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(
            "\"host.shutdown_timeout_seconds\": 20",
            "\"calendar.vacation_confirmer\": \"owner\""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "settings.calendar.vacation_confirmer"));
    }

    [Fact]
    public void Non_loopback_status_bind_is_held_for_approval_with_exit_code_2()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"statusBind\": \"loopback\"", "\"statusBind\": \"lan\""), RegistrySetupSettingCatalog.Known);

        Assert.NotNull(result.Plan);
        Assert.NotNull(result.PlanHash);
        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Hold && issue.Path == "network.statusBind");
        Assert.Equal(SetupExitCode.Held, result.ExitCode);
    }

    [Fact]
    public void An_error_and_a_hold_together_exit_1()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(
            "\"statusBind\": \"loopback\"",
            "\"statusBind\": \"lan\", \"statusPort\": 0"), RegistrySetupSettingCatalog.Known);

        Assert.Null(result.Plan);
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public void Status_port_set_in_both_network_and_settings_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(
            "\"settings\": { \"host.shutdown_timeout_seconds\": 20 }",
            "\"settings\": { \"host.shutdown_timeout_seconds\": 20, \"host.status.port\": 9000 }"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "settings.host.status.port"));
    }

    [Fact]
    public void Enabling_the_scam_mail_protocol_with_email_on_is_held_for_owner_approval()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid
            .Replace("\"email\": { \"enabled\": false }", "\"email\": { \"enabled\": true }", StringComparison.Ordinal)
            .Replace("\"scamMailProtocol\": { \"enabled\": false }", "\"scamMailProtocol\": { \"enabled\": true }", StringComparison.Ordinal), RegistrySetupSettingCatalog.Known);

        Assert.NotNull(result.Plan);
        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Hold && issue.Path == "scamMailProtocol.enabled");
        Assert.Equal(SetupExitCode.Held, result.ExitCode);
    }

    [Fact]
    public void Enabling_the_scam_mail_protocol_with_email_off_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"scamMailProtocol\": { \"enabled\": false }", "\"scamMailProtocol\": { \"enabled\": true }"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "scamMailProtocol.enabled"));
        Assert.Null(result.Plan);
    }

    [Fact]
    public void The_scam_mail_setting_cannot_be_set_through_the_settings_map()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(
            "\"settings\": { \"host.shutdown_timeout_seconds\": 20 }",
            "\"settings\": { \"host.shutdown_timeout_seconds\": 20, \"security.scam_mail.protocol_enabled\": true }"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "settings.security.scam_mail.protocol_enabled"));
    }

    [Fact]
    public void The_scam_mail_protocol_off_adds_no_issue()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);

        Assert.DoesNotContain(result.Issues, issue => issue.Path.StartsWith("scamMailProtocol", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"source\": \"template:starter@1.0.0\"", true)]
    [InlineData("\"source\": \"blank\"", true)]
    [InlineData("\"source\": \"import:dump-2026_11\"", true)]
    [InlineData("\"source\": \"template:starter@1.0\"", false)]
    [InlineData("\"source\": \"template:@1.0.0\"", false)]
    [InlineData("\"source\": \"import:\"", false)]
    [InlineData("\"source\": \"bogus\"", false)]
    public void Company_source_grammar(string sourceFragment, bool valid)
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"source\": \"template:starter@1.0.0\"", sourceFragment), RegistrySetupSettingCatalog.Known);

        Assert.Equal(valid, !HasError(result, "company.source"));
    }

    [Fact]
    public void Backups_without_a_target_need_the_acknowledgement()
    {
        var missing = SetupPlanLoader.Load(SetupFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"), RegistrySetupSettingCatalog.Known);
        var acknowledged = SetupPlanLoader.Load(SetupFixtures.With(
            "\"target\": \"file:/mnt/backup/bazaar\", \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": false",
            "\"target\": null, \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": true"), RegistrySetupSettingCatalog.Known);

        Assert.Contains(missing.Issues, issue =>
            issue.Path == "backups.target" && issue.Severity == SetupConstants.MissingBackupTargetSeverity);
        Assert.Equal(SetupExitCode.Held, SetupExitCodes.ForIssues(missing.Issues));
        Assert.Empty(acknowledged.Issues);
    }

    [Fact]
    public void Duplicate_model_source_ids_are_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"id\": \"anthropic-owner\"", "\"id\": \"freellmapi\""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "models.sources[1].id"));
    }

    [Fact]
    public void Non_https_audit_anchor_and_bad_module_names_are_rejected()
    {
        var anchor = SetupPlanLoader.Load(SetupFixtures.With("\"https://anchor.example.invalid/bazaar-audit\"", "\"http://anchor.example.invalid/x\""), RegistrySetupSettingCatalog.Known);
        var module = SetupPlanLoader.Load(SetupFixtures.With("\"Bazaar.GC\"", "\"gc\""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(anchor, "custody.auditAnchorTarget"));
        Assert.True(HasError(module, "modules[0]"));
    }

    [Fact]
    public void Privacy_profile_other_than_private_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"profile\": \"private\"", "\"profile\": \"public\""), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "privacy.profile"));
    }

    [Fact]
    public void A_null_where_no_null_is_allowed_is_rejected()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("\"locale\": \"en\"", "\"locale\": null"), RegistrySetupSettingCatalog.Known);

        Assert.True(HasError(result, "install.locale"));
    }

    private static JsonObject ReverseKeys(JsonObject source)
    {
        var pairs = source.Select(p => (p.Key, Value: p.Value?.DeepClone())).Reverse().ToList();
        var result = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            result[key] = value is JsonObject obj ? ReverseKeys(obj) : value;
        }

        return result;
    }
}
