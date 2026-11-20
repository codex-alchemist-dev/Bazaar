using System.Text.Json;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup.Verification;

/// <summary>Each pure check, in its green and its non-green cases. The plans are built directly, so each case changes one thing.</summary>
public sealed class SetupPureChecksTests
{
    private const string _anchorUrl = "https://anchor.example.invalid/bazaar-audit";

    /// <summary>A plan that passes every pure check, with the same shape as the fixture.</summary>
    private static SetupCustodySection ValidCustody(string? identityKek = null) => new()
    {
        IdentityKek = identityKek,
        KekProvider = "dpapi",
        KekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
        AuditAnchorTarget = _anchorUrl,
        MergeCredential = new SetupMergeCredentialSection { Kind = "owner_app", AppIdRef = "secret://bazaar/owner/app-id" },
    };

    /// <summary>Builds a plan from parts. Each part not given is the valid default. Shared with the catalogue tests in this file.</summary>
    internal static SetupPlan PlanWith(
        SetupNetworkSection? network = null,
        SetupBackupsSection? backups = null,
        SetupOwnerSection? owner = null,
        SetupCustodySection? custody = null,
        SetupModelsSection? models = null,
        Dictionary<string, JsonElement>? settings = null) => new()
        {
            Network = network ?? new SetupNetworkSection(),
            Backups = backups ?? new SetupBackupsSection { Target = "file:/mnt/backup/bazaar" },
            Owner = owner ?? new SetupOwnerSection { DisplayName = "Owner" },
            Custody = custody ?? ValidCustody(),
            Models = models ?? new SetupModelsSection
            {
                Sources = [new SetupModelSource { Id = "freellmapi", KeyRef = "secret://bazaar/providers/freellmapi/key" }],
            },
            Settings = settings ?? new(),
        };

    private static Task<SetupCheckResult> Run(ISetupVerificationCheck check, SetupPlan plan) =>
        check.RunAsync(new SetupVerificationContext(plan), CancellationToken.None);

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    private static SetupPlan TpmPlan(string? identityKek, bool? allowVirtual)
    {
        var custody = ValidCustody(identityKek);
        var settings = new Dictionary<string, JsonElement>();
        if (allowVirtual is { } allow)
            settings[IdentityKekCustodyCheck.AllowVirtualKey] = JsonSerializer.SerializeToElement(allow);
        return PlanWith(custody: custody, settings: settings);
    }

    [Theory]
    [InlineData(null, null, SetupCheckStatus.Green)]
    [InlineData("file", true, SetupCheckStatus.Green)]
    [InlineData("openbao", null, SetupCheckStatus.Green)]
    [InlineData("tpm", null, SetupCheckStatus.Green)]
    [InlineData("tpm", false, SetupCheckStatus.Green)]
    [InlineData("tpm", true, SetupCheckStatus.Amber)]
    public async Task Identity_kek_custody_warns_only_for_tpm_with_virtual_allowed(string? kek, bool? allowVirtual, SetupCheckStatus expected)
    {
        var result = await Run(new IdentityKekCustodyCheck(), TpmPlan(kek, allowVirtual));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Fixture_plan_passes_every_pure_check()
    {
        var plan = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

        foreach (var check in SetupVerificationChecks.Pure)
        {
            var result = await Run(check, plan);
            Assert.Equal(SetupCheckStatus.Green, result.Status);
        }
    }

    [Fact]
    public async Task Status_bind_is_green_on_loopback()
    {
        var result = await Run(new StatusBindCheck(), PlanWith(network: new SetupNetworkSection { StatusBind = "loopback" }));

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task Status_bind_off_loopback_is_red_because_no_approval_is_recorded()
    {
        var result = await Run(new StatusBindCheck(), PlanWith(network: new SetupNetworkSection { StatusBind = "lan" }));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("network.statusBind", result.Message);
        Assert.NotNull(result.Remediation);
        Assert.DoesNotContain("\"lan\"", result.Message);
    }

    [Fact]
    public async Task Backup_target_is_green_when_configured()
    {
        var result = await Run(new BackupTargetCheck(), PlanWith());

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task Backup_target_with_only_the_acknowledgement_is_amber_and_hides_nothing()
    {
        var result = await Run(new BackupTargetCheck(), PlanWith(backups: new SetupBackupsSection { Target = null, AcknowledgeNoTarget = true }));

        Assert.Equal(SetupCheckStatus.Amber, result.Status);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task Backup_target_with_no_target_and_no_acknowledgement_is_red()
    {
        var result = await Run(new BackupTargetCheck(), PlanWith(backups: new SetupBackupsSection { Target = null, AcknowledgeNoTarget = false }));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task Blank_backup_target_is_red()
    {
        var result = await Run(new BackupTargetCheck(), PlanWith(backups: new SetupBackupsSection { Target = "   " }));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
    }

    [Fact]
    public async Task Backup_target_text_is_never_echoed()
    {
        const string target = "file:/mnt/backup/bazaar-private-location";
        var result = await Run(new BackupTargetCheck(), PlanWith(backups: new SetupBackupsSection { Target = target }));

        Assert.DoesNotContain("private-location", result.Message);
    }

    [Fact]
    public async Task Second_factor_is_green_when_required()
    {
        var result = await Run(new OwnerSecondFactorCheck(), PlanWith(owner: new SetupOwnerSection { DisplayName = "Owner", RequireSecondFactor = true }));

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task Second_factor_not_required_is_red()
    {
        var result = await Run(new OwnerSecondFactorCheck(), PlanWith(owner: new SetupOwnerSection { DisplayName = "Owner", RequireSecondFactor = false }));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("owner.requireSecondFactor", result.Message);
    }

    [Fact]
    public async Task Secret_references_are_green_when_all_are_well_formed()
    {
        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith());

        Assert.Equal(SetupCheckStatus.Green, result.Status);
        Assert.Contains("None is resolved here", result.Message);
    }

    [Fact]
    public async Task A_literal_bootstrap_value_is_red_and_its_text_is_not_shown()
    {
        var custody = new SetupCustodySection
        {
            KekProvider = "dpapi",
            KekRef = SetupFixtures.Canary,
            AuditAnchorTarget = _anchorUrl,
            MergeCredential = ValidCustody().MergeCredential,
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(custody: custody));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("custody.kekRef", result.Message);
        Assert.DoesNotContain(SetupFixtures.Canary, result.Message);
        Assert.DoesNotContain(SetupFixtures.Canary, result.Remediation ?? string.Empty);
    }

    [Fact]
    public async Task Bootstrap_reference_must_use_the_family_of_its_provider()
    {
        var custody = new SetupCustodySection
        {
            KekProvider = "env",
            KekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            AuditAnchorTarget = _anchorUrl,
            MergeCredential = ValidCustody().MergeCredential,
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(custody: custody));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("custody.kekRef", result.Message);
    }

    [Fact]
    public async Task Owner_app_without_its_app_id_reference_is_red()
    {
        var custody = new SetupCustodySection
        {
            KekProvider = "dpapi",
            KekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            AuditAnchorTarget = _anchorUrl,
            MergeCredential = new SetupMergeCredentialSection { Kind = "owner_app", AppIdRef = null },
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(custody: custody));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("custody.mergeCredential.appIdRef", result.Message);
    }

    [Fact]
    public async Task Owner_linked_credential_needs_no_app_id_reference()
    {
        var custody = new SetupCustodySection
        {
            KekProvider = "dpapi",
            KekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            AuditAnchorTarget = _anchorUrl,
            MergeCredential = new SetupMergeCredentialSection { Kind = "owner_linked", AppIdRef = null },
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(custody: custody));

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task A_literal_model_key_is_red_and_names_the_path_only()
    {
        var models = new SetupModelsSection
        {
            Sources = [new SetupModelSource { Id = "freellmapi", KeyRef = SetupFixtures.Canary }],
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(models: models));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("models.sources[0].keyRef", result.Message);
        Assert.DoesNotContain(SetupFixtures.Canary, result.Message);
    }

    [Fact]
    public async Task A_model_key_of_the_wrong_family_is_red()
    {
        var models = new SetupModelsSection
        {
            Sources = [new SetupModelSource { Id = "freellmapi", KeyRef = "file:/etc/bazaar/key" }],
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(models: models));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
    }

    [Fact]
    public async Task A_model_source_without_a_key_is_allowed()
    {
        var models = new SetupModelsSection
        {
            Sources = [new SetupModelSource { Id = "local-runtime", KeyRef = null }],
        };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(models: models));

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Fact]
    public async Task A_secret_named_setting_with_a_literal_is_red_and_its_value_is_not_shown()
    {
        var settings = new Dictionary<string, JsonElement> { ["host.api_token"] = Text(SetupFixtures.Canary) };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(settings: settings));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("settings.host.api_token", result.Message);
        Assert.DoesNotContain(SetupFixtures.Canary, result.Message);
    }

    [Fact]
    public async Task A_secret_named_setting_with_a_store_reference_is_green()
    {
        var settings = new Dictionary<string, JsonElement> { ["host.api_token"] = Text("secret://bazaar/host/api-token") };

        var result = await Run(new SecretReferenceSyntaxCheck(), PlanWith(settings: settings));

        Assert.Equal(SetupCheckStatus.Green, result.Status);
    }

    [Theory]
    [InlineData("host.api_token", true)]
    [InlineData("host.db_password", true)]
    [InlineData("host.credential_path", true)]
    [InlineData("host.shutdown_timeout_seconds", false)]
    public void Secret_looking_setting_names_are_recognised(string key, bool expected)
    {
        Assert.Equal(expected, SecretReferenceSyntaxCheck.IsSecretNamedSetting(key));
    }

    [Fact]
    public async Task Audit_anchor_target_is_green_when_set_and_never_echoed()
    {
        var result = await Run(new AuditAnchorTargetCheck(), PlanWith());

        Assert.Equal(SetupCheckStatus.Green, result.Status);
        Assert.DoesNotContain("anchor.example.invalid", result.Message);
    }

    [Fact]
    public async Task Audit_anchor_target_unset_is_red_because_the_setup_doc_requires_it()
    {
        var custody = new SetupCustodySection
        {
            KekProvider = "dpapi",
            KekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            AuditAnchorTarget = null,
            MergeCredential = ValidCustody().MergeCredential,
        };

        var result = await Run(new AuditAnchorTargetCheck(), PlanWith(custody: custody));

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("custody.auditAnchorTarget", result.Message);
    }
}

/// <summary>The registered checks as a set: the placeholders, the full report on the fixture, and no value in any report.</summary>
public sealed class SetupVerificationCatalogueTests
{
    /// <summary>The ids of the placeholders, in the order the catalogue lists them.</summary>
    private static readonly string[] _placeholderIds =
    [
        "schema.migrations", "instance.lock", "database.postgres", "providers.roles", "secrets.resolution",
        "audit.chainHead", "audit.anchorReachable", "backups.reachable", "backups.drill", "host.clock", "host.disk",
        "models.reachable", "pairing.offer", "server.identity",
    ];

    /// <summary>Every text a report carries, so a leak test can look at all of it.</summary>
    private static string AllReportText(SetupVerificationReport report) => string.Join(
        "\n",
        report.Checks.SelectMany(row => new[] { row.Id, row.Title, row.Message, row.Remediation ?? string.Empty }));

    [Fact]
    public async Task Every_placeholder_reports_not_evaluated_with_its_reason_and_never_green()
    {
        var context = new SetupVerificationContext(SetupPureChecksTests.PlanWith());

        foreach (var check in SetupVerificationChecks.Infrastructure)
        {
            var result = await check.RunAsync(context, CancellationToken.None);

            Assert.Equal(SetupCheckStatus.NotEvaluated, result.Status);
            Assert.Equal("Not evaluated: needs the Host adapter.", result.Message);
        }
    }

    [Fact]
    public void The_placeholders_are_the_doc_45_checks_this_slice_does_not_run()
    {
        Assert.Equal(_placeholderIds, SetupVerificationChecks.Infrastructure.Select(check => check.Id));
    }

    [Fact]
    public void Every_registered_check_has_a_distinct_id()
    {
        var ids = SetupVerificationChecks.All.Select(check => check.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SetupVerificationChecks.Pure.Count + SetupVerificationChecks.Infrastructure.Count, ids.Count);
    }

    [Fact]
    public async Task The_fixture_report_is_amber_with_every_placeholder_as_an_amber_item()
    {
        var verifier = new SetupVerifier(SetupVerificationChecks.All);
        var plan = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

        var report = await verifier.VerifyAsync(new SetupVerificationContext(plan), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.Amber, report.Overall);
        // Five pure checks are green (no cost). Fourteen placeholders cost 5 each, so the score is 100 - 70.
        Assert.Equal(30, report.Score);
        Assert.Equal(SetupVerificationChecks.Pure.Count, report.Checks.Count(row => row.Status == SetupCheckStatus.Green));
        Assert.Equal(_placeholderIds, report.AmberItems.Select(item => item.Id));
    }

    [Fact]
    public async Task The_fixture_report_completes_only_when_every_amber_item_is_acknowledged()
    {
        var verifier = new SetupVerifier(SetupVerificationChecks.All);
        var plan = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;
        var report = await verifier.VerifyAsync(new SetupVerificationContext(plan), TestContext.Current.CancellationToken);

        Assert.False(report.CanComplete([]));
        Assert.False(report.CanComplete(_placeholderIds.Take(13)));
        Assert.True(report.CanComplete(_placeholderIds));
    }

    [Fact]
    public async Task No_report_text_carries_a_canary_value_or_a_reference_text()
    {
        // Every secret-typed place holds the canary or a reference of the wrong family, so each pure check has something to name.
        var custody = new SetupCustodySection
        {
            KekProvider = "dpapi",
            KekRef = SetupFixtures.Canary,
            AuditAnchorTarget = null,
            MergeCredential = new SetupMergeCredentialSection { Kind = "owner_app", AppIdRef = SetupFixtures.Canary },
        };
        var models = new SetupModelsSection
        {
            Sources =
            [
                new SetupModelSource { Id = "freellmapi", KeyRef = SetupFixtures.Canary },
                new SetupModelSource { Id = "anthropic-owner", KeyRef = "file:secret://bazaar/providers/anthropic/key" },
            ],
        };
        var settings = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["host.api_token"] = System.Text.Json.JsonSerializer.SerializeToElement(SetupFixtures.Canary),
        };
        var plan = SetupPureChecksTests.PlanWith(
            backups: new SetupBackupsSection { Target = null, AcknowledgeNoTarget = false },
            custody: custody,
            models: models,
            settings: settings);

        var verifier = new SetupVerifier(SetupVerificationChecks.All);
        var report = await verifier.VerifyAsync(new SetupVerificationContext(plan), TestContext.Current.CancellationToken);

        var text = AllReportText(report);
        Assert.Equal(SetupCheckStatus.Red, report.Overall);
        Assert.DoesNotContain(SetupFixtures.Canary, text);
        Assert.DoesNotContain("providers/anthropic", text);
        Assert.DoesNotContain("secret://", text);
    }
}
