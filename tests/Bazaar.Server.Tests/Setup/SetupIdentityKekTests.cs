using System.Text;
using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The <c>custody.identityKek</c> plan field (doc 45 step 5; doc 14 <c>identity.kek_provider</c>, applied to the host configuration file). It is separate from the bootstrap-secret
/// field <c>custody.kekProvider</c>, optional, and left out of the canonical form when absent so existing plan hashes do not change.
/// </summary>
public sealed class SetupIdentityKekTests
{
    private const string _bootstrapFragment = "\"kekProvider\": \"dpapi\",";

    private static string PlanJson(string identityKek) => SetupFixtures.With(_bootstrapFragment, _bootstrapFragment + " \"identityKek\": " + identityKek + ",");

    private static SetupPlan Valid() => SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

    [Fact]
    public void An_absent_field_is_left_out_of_the_canonical_form_so_the_hash_is_unchanged()
    {
        var plan = Valid();

        var canonical = Encoding.UTF8.GetString(SetupPlanCanonicalizer.ToCanonicalUtf8(plan));

        Assert.Null(plan.Custody.IdentityKek);
        Assert.DoesNotContain("identityKek", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("custody.identityKek", SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(plan)).Keys);
        Assert.Equal(SetupPlanCanonicalizer.Hash(plan), SetupPlanCanonicalizer.Hash(SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!));
    }

    [Fact]
    public void A_set_field_is_in_the_canonical_form_and_changes_the_hash()
    {
        var plan = SetupPlanLoader.Load(PlanJson("\"tpm\""), RegistrySetupSettingCatalog.Known).Plan!;

        Assert.Contains("\"identityKek\":\"tpm\"", Encoding.UTF8.GetString(SetupPlanCanonicalizer.ToCanonicalUtf8(plan)), StringComparison.Ordinal);
        Assert.NotEqual(SetupPlanCanonicalizer.Hash(Valid()), SetupPlanCanonicalizer.Hash(plan));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("dpapi")]
    [InlineData("openbao")]
    [InlineData("tpm")]
    public void Every_allowed_value_loads_clean(string value)
    {
        var result = SetupPlanLoader.Load(PlanJson("\"" + value + "\""), RegistrySetupSettingCatalog.Known);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
        Assert.Equal(value, result.Plan!.Custody.IdentityKek);
    }

    [Fact]
    public void An_explicit_null_means_the_server_default()
    {
        var result = SetupPlanLoader.Load(PlanJson("null"), RegistrySetupSettingCatalog.Known);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
        Assert.Null(result.Plan!.Custody.IdentityKek);
    }

    [Theory]
    [InlineData("\"env\"")]
    [InlineData("\"systemd-creds\"")]
    [InlineData("\"nonsense\"")]
    [InlineData("\"\"")]
    public void An_unknown_value_is_an_error_that_does_not_repeat_it(string value)
    {
        var result = SetupPlanLoader.Load(PlanJson(value), RegistrySetupSettingCatalog.Known);

        var issue = Assert.Single(result.Issues, i => i.Path == "custody.identityKek");
        Assert.Equal(SetupIssueSeverity.Error, issue.Severity);
        Assert.DoesNotContain("nonsense", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bootstrap_provider_list_is_unchanged()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(_bootstrapFragment, "\"kekProvider\": \"tpm\","), RegistrySetupSettingCatalog.Known);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "custody.kekProvider");
    }

    [Fact]
    public void A_plan_that_leaves_the_field_out_loads_clean()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void The_field_is_not_projected_onto_a_registry_key()
    {
        var set = SetupSettingProjection.Project(SetupPlanLoader.Load(PlanJson("\"openbao\""), RegistrySetupSettingCatalog.Known).Plan!);

        Assert.DoesNotContain(set, s => s.PlanPath == "custody.identityKek");
        Assert.DoesNotContain(set, s => s.Key == "identity.kek_provider");
        Assert.Null(SetupSettingProjection.DerivedKeyForPath("custody.identityKek"));
    }

    [Fact]
    public void The_field_has_its_own_section_that_a_step_must_cover()
    {
        var plan = SetupPlanLoader.Load(PlanJson("\"file\""), RegistrySetupSettingCatalog.Known).Plan!;

        var requested = SetupPlanSections.Requested(plan).Select(section => section.Id).ToList();
        var section = Assert.Single(SetupPlanSections.All, s => s.Id == "custody.identityKek");

        Assert.Contains("custody.identityKek", requested);
        Assert.Equal([5], section.DocSteps);
        Assert.DoesNotContain("custody.identityKek", SetupPlanSections.Requested(Valid()).Select(s => s.Id));
    }

    [Fact]
    public void The_wizard_asks_an_optional_question_after_the_bootstrap_key_reference()
    {
        var questions = SetupWizardSteps.Create().SelectMany(step => step.Questions).ToList();
        var ids = questions.Select(q => q.Id).ToList();
        var question = Assert.Single(questions, q => q.Id == "custody.identityKek");

        Assert.Equal(ids.IndexOf("custody.kekRef") + 1, ids.IndexOf("custody.identityKek"));
        Assert.Equal("custody.identityKek", question.PlanPath);
        Assert.True(question.Optional);
        Assert.Null(question.Validate!("tpm"));
        Assert.Null(question.Validate("openbao"));
        Assert.NotNull(question.Validate("systemd-creds"));
        Assert.NotNull(question.Validate("nonsense"));
    }
}
