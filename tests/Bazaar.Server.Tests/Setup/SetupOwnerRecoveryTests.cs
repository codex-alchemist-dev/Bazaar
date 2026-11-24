using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The owner section of the plan (doc 45 step 6): the recovery bundle reference is a reference only, never a secret, and the owner
/// leaves split into three sections (owner, owner.recovery, owner.backupOwners) so a step can apply two and hold the third.
/// </summary>
public sealed class SetupOwnerRecoveryTests
{
    private const string _recoveryOld = "\"recovery\": { \"printedCodes\": false, \"backupOwners\": [] }";

    private static string WithRecovery(string recoveryJson) => SetupFixtures.With(_recoveryOld, $"\"recovery\": {recoveryJson}");

    private static SetupLoadResult Load(string json) => SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

    private static IEnumerable<string> RequestedIds(SetupPlan plan) => SetupPlanSections.Requested(plan).Select(section => section.Id);

    [Fact]
    public void A_bundle_reference_in_the_secret_store_is_accepted_and_kept_as_given()
    {
        var result = Load(WithRecovery("""{ "printedCodes": true, "bundleRef": "secret://bazaar/owner/recovery-bundle", "backupOwners": [] }"""));

        Assert.NotNull(result.Plan);
        Assert.Equal("secret://bazaar/owner/recovery-bundle", result.Plan.Owner.Recovery.BundleRef);
        Assert.True(result.Plan.Owner.Recovery.PrintedCodes);
    }

    [Theory]
    [InlineData(SetupFixtures.Canary)]
    [InlineData("env:BAZAAR_RECOVERY")]
    [InlineData("file:/etc/recovery.bundle")]
    public void A_bundle_value_that_is_not_a_secret_store_reference_is_rejected_without_echoing_it(string value)
    {
        var result = Load(WithRecovery($$"""{ "printedCodes": false, "bundleRef": "{{value}}", "backupOwners": [] }"""));

        Assert.Null(result.Plan);
        Assert.Contains(result.Issues, issue => issue.Path == "owner.recovery.bundleRef");
        Assert.All(result.Issues, issue => Assert.DoesNotContain(value, issue.Message + issue.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void A_null_bundle_reference_is_the_same_as_omitting_it()
    {
        var withNull = Load(WithRecovery("""{ "printedCodes": false, "bundleRef": null, "backupOwners": [] }"""));
        var without = Load(SetupFixtures.Valid);

        Assert.NotNull(withNull.Plan);
        Assert.NotNull(without.Plan);
        Assert.Null(withNull.Plan.Owner.Recovery.BundleRef);
        Assert.Equal(SetupPlanCanonicalizer.Hash(without.Plan), SetupPlanCanonicalizer.Hash(withNull.Plan));
    }

    [Fact]
    public void A_plan_without_a_bundle_reference_keeps_the_key_out_of_its_canonical_form()
    {
        var node = SetupPlanCanonicalizer.ToNode(Load(SetupFixtures.Valid).Plan!);

        Assert.DoesNotContain("bundleRef", node!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_recovery_key_is_still_rejected()
    {
        Assert.Null(Load(WithRecovery("""{ "printedCodes": false, "recoveryPhrase": "x", "backupOwners": [] }""")).Plan);
    }

    [Fact]
    public void The_owner_section_is_requested_alone_for_a_plan_with_a_name_and_no_recovery_choices()
    {
        var ids = RequestedIds(Load(SetupFixtures.Valid).Plan!).ToList();

        Assert.Contains("owner", ids);
        Assert.DoesNotContain("owner.recovery", ids);
        Assert.DoesNotContain("owner.backupOwners", ids);
    }

    [Fact]
    public void A_bundle_reference_or_printed_codes_request_the_recovery_section_and_backup_owners_request_their_own()
    {
        var bundle = RequestedIds(Load(WithRecovery("""{ "printedCodes": false, "bundleRef": "secret://bazaar/owner/b", "backupOwners": [] }""")).Plan!).ToList();
        var printed = RequestedIds(Load(WithRecovery("""{ "printedCodes": true, "backupOwners": [] }""")).Plan!).ToList();
        var backups = RequestedIds(Load(WithRecovery("""{ "printedCodes": false, "backupOwners": ["alice"] }""")).Plan!).ToList();

        Assert.Contains("owner.recovery", bundle);
        Assert.DoesNotContain("owner.backupOwners", bundle);
        Assert.Contains("owner.recovery", printed);
        Assert.Contains("owner.backupOwners", backups);
        Assert.DoesNotContain("owner.recovery", backups);
    }

    [Fact]
    public void The_three_owner_sections_name_doc_45_step_6()
    {
        var text = SetupPlanSections.Describe(SetupPlanSections.ByIds(["owner", "owner.recovery", "owner.backupOwners"]));

        Assert.Equal("owner (doc 45 step 6), owner.recovery (doc 45 step 6), owner.backupOwners (doc 45 step 6)", text);
    }
}
