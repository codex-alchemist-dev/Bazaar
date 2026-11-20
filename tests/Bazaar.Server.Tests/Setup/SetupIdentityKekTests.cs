using System.Text;
using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The <c>custody.identityKek</c> plan field (doc 45 step 5; doc 14 <c>identity.kek_provider</c>). It is separate from the bootstrap-secret
/// field <c>custody.kekProvider</c>, optional, and left out of the canonical form when absent so existing plan hashes do not change.
/// </summary>
public sealed class SetupIdentityKekTests
{
    private const string _key = "identity.kek_provider";
    private const string _bootstrapFragment = "\"kekProvider\": \"dpapi\",";

    private static readonly ISetupSettingCatalog _withKey = new CatalogWithIdentityKek();

    private static string PlanJson(string identityKek) => SetupFixtures.With(_bootstrapFragment, _bootstrapFragment + " \"identityKek\": " + identityKek + ",");

    private static SetupPlan Valid() => SetupPlanLoader.Load(SetupFixtures.Valid, _withKey).Plan!;

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
        var plan = SetupPlanLoader.Load(PlanJson("\"tpm\""), _withKey).Plan!;

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
        var result = SetupPlanLoader.Load(PlanJson("\"" + value + "\""), _withKey);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
        Assert.Equal(value, result.Plan!.Custody.IdentityKek);
    }

    [Fact]
    public void An_explicit_null_means_the_server_default()
    {
        var result = SetupPlanLoader.Load(PlanJson("null"), _withKey);

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
        var result = SetupPlanLoader.Load(PlanJson(value), _withKey);

        var issue = Assert.Single(result.Issues, i => i.Path == "custody.identityKek");
        Assert.Equal(SetupIssueSeverity.Error, issue.Severity);
        Assert.DoesNotContain("nonsense", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bootstrap_provider_list_is_unchanged()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With(_bootstrapFragment, "\"kekProvider\": \"tpm\","), _withKey);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "custody.kekProvider");
    }

    [Fact]
    public void A_build_without_the_registry_key_refuses_a_plan_that_sets_the_field()
    {
        Assert.False(RegistrySetupSettingCatalog.Known.TryGet(_key, out _), "this test documents a tree where the key is not registered yet");

        var result = SetupPlanLoader.Load(PlanJson("\"file\""), RegistrySetupSettingCatalog.Known);

        var issue = Assert.Single(result.Issues, i => i.Path == "custody.identityKek");
        Assert.Equal(SetupIssueSeverity.Error, issue.Severity);
        Assert.Contains("not registered", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_without_the_registry_key_still_accepts_a_plan_that_leaves_the_field_out()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void Setting_the_registry_key_in_the_settings_map_too_is_one_error()
    {
        var json = PlanJson("\"file\"").Replace("\"settings\": {", "\"settings\": { \"" + _key + "\": \"file\",", StringComparison.Ordinal);

        var result = SetupPlanLoader.Load(json, _withKey);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "settings." + _key && issue.Message.Contains("already set by custody.identityKek", StringComparison.Ordinal));
    }

    [Fact]
    public void The_projection_writes_the_key_only_when_the_field_is_set()
    {
        var absent = SetupSettingProjection.Project(Valid());
        var set = SetupSettingProjection.Project(SetupPlanLoader.Load(PlanJson("\"openbao\""), _withKey).Plan!);

        Assert.DoesNotContain(absent, s => s.Key == _key);
        var projected = Assert.Single(set, s => s.Key == _key);
        Assert.Equal("custody.identityKek", projected.PlanPath);
        Assert.Equal("openbao", projected.Value.GetString());
        Assert.Equal(_key, SetupSettingProjection.DerivedKeyForPath("custody.identityKek"));
    }

    [Fact]
    public void The_field_has_its_own_section_that_a_step_must_cover()
    {
        var plan = SetupPlanLoader.Load(PlanJson("\"file\""), _withKey).Plan!;

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

    /// <summary>The real registry plus a stand-in for the <c>identity.kek_provider</c> key, which the server registry does not hold yet.</summary>
    private sealed class CatalogWithIdentityKek : ISetupSettingCatalog
    {
        private readonly IdentityKekDefinition _definition = new();

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

    private sealed class IdentityKekDefinition : ISetupSettingDefinition
    {
        private static readonly string[] _allowed = ["file", "dpapi", "openbao", "tpm"];

        public string Name => _key;

        public bool IsInstallScope => true;

        public Type ValueType => typeof(string);

        public string? CheckValue(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && _allowed.Contains(value.GetString(), StringComparer.Ordinal) ? null : "must be file, dpapi, openbao or tpm.";

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) => new(true, null, proposed.GetRawText());
    }
}
