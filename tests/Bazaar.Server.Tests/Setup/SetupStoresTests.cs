using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The three plan parts that now have a backing store (doc 45 steps 9, 10 and 13): model sources, the merge credential choice and
/// the company source. Pure checks of the catalogue, the validator, the registry keys and the projection. No database.
/// </summary>
public sealed class SetupStoresTests
{
    private static readonly string[] _authKinds = ["api_key", "bearer", "none"];
    private static readonly string[] _adapters = ["openai_chat", "anthropic_messages", "gemini_generate_content"];
    private static readonly string[] _kinds = ["first_party", "aggregator", "gateway", "reverse_proxy", "local"];

    private static SetupLoadResult Load(string json) => SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

    private static bool HasError(SetupLoadResult result, string path) =>
        result.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    private static JsonElement Json(string text) => JsonSerializer.SerializeToElement(text);

    [Fact]
    public void The_default_catalogue_resolves_the_doc_45_example_ids_to_the_gateway_facts()
    {
        Assert.True(ModelSourceCatalogue.Default.TryGet("freellmapi", out var free));
        Assert.Equal(("gateway", "openai_chat", "bearer"), (free!.Kind, free.Adapter, free.AuthKind));
        Assert.Equal("http://127.0.0.1:3001/v1", free.BaseUrl);

        Assert.True(ModelSourceCatalogue.Default.TryGet("anthropic-owner", out var anthropic));
        Assert.Equal("anthropic-direct", anthropic!.Id);
        Assert.Equal("anthropic_messages", anthropic.Adapter);
    }

    [Fact]
    public void A_source_doc_05_leaves_to_the_operator_is_not_in_the_catalogue_so_nothing_is_invented()
    {
        foreach (var id in new[] { "groq", "mistral", "xai", "deepseek", "cerebras", "cloudflare-workers-ai", "omniroute", "litellm" })
        {
            Assert.False(ModelSourceCatalogue.Default.TryGet(id, out _), id);
        }
    }

    [Fact]
    public void Every_catalogue_entry_uses_a_credential_kind_the_gateway_table_accepts_and_a_local_one_takes_no_key()
    {
        foreach (var entry in ModelSourceCatalogue.Default.Entries)
        {
            Assert.Contains(entry.AuthKind, _authKinds);
            Assert.Contains(entry.Adapter, _adapters);
            Assert.Contains(entry.Kind, _kinds);
            Assert.Equal(entry.Kind == "local", !entry.TakesKey);
            Assert.True(Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo), entry.Id);
        }
    }

    [Fact]
    public void A_name_listed_twice_in_a_catalogue_is_refused()
    {
        var entry = new ModelSourceEntry("a", "local", "openai_chat", "http://127.0.0.1:1/v1", "none", ["b"]);
        var other = new ModelSourceEntry("b", "local", "openai_chat", "http://127.0.0.1:2/v1", "none", []);

        Assert.Throws<ArgumentException>(() => new ModelSourceCatalogue([entry, other]));
    }

    [Fact]
    public void The_valid_plan_still_loads_with_its_doc_45_model_sources()
    {
        var result = Load(SetupFixtures.Valid);

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void A_source_outside_the_catalogue_is_an_error_that_names_the_path_and_not_the_id()
    {
        var result = Load(SetupFixtures.With("\"id\": \"freellmapi\"", "\"id\": \"canary-secret-0000\""));

        Assert.True(HasError(result, "models.sources[0].id"));
        Assert.DoesNotContain(SetupFixtures.Canary, string.Join(" ", result.Issues.Select(issue => issue.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_and_its_alias_are_one_source()
    {
        var result = Load(SetupFixtures.With("\"id\": \"freellmapi\"", "\"id\": \"anthropic\""));

        Assert.True(HasError(result, "models.sources[0].id") || HasError(result, "models.sources[1].id"));
    }

    [Fact]
    public void A_source_that_takes_a_key_must_carry_one()
    {
        var result = Load(SetupFixtures.With(
            "{ \"id\": \"freellmapi\", \"keyRef\": \"secret://bazaar/providers/freellmapi/key\" }",
            "{ \"id\": \"freellmapi\" }"));

        Assert.True(HasError(result, "models.sources[0].keyRef"));
    }

    [Fact]
    public void A_local_runtime_takes_no_key()
    {
        var result = Load(SetupFixtures.With(
            "{ \"id\": \"freellmapi\", \"keyRef\": \"secret://bazaar/providers/freellmapi/key\" }",
            "{ \"id\": \"ollama\", \"keyRef\": \"secret://bazaar/providers/ollama/key\" }"));

        Assert.True(HasError(result, "models.sources[0].keyRef"));
    }

    [Fact]
    public void A_local_runtime_without_a_key_is_valid()
    {
        var result = Load(SetupFixtures.With(
            "{ \"id\": \"freellmapi\", \"keyRef\": \"secret://bazaar/providers/freellmapi/key\" }",
            "{ \"id\": \"ollama\" }"));

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public void A_key_reference_outside_the_providers_path_is_an_error()
    {
        var result = Load(SetupFixtures.With("secret://bazaar/providers/freellmapi/key", "secret://bazaar/owner/freellmapi-key"));

        Assert.True(HasError(result, "models.sources[0].keyRef"));
    }

    [Theory]
    [InlineData("Free Lane")]
    [InlineData("-free")]
    [InlineData("free_lane")]
    public void A_default_preset_that_is_not_a_slug_is_an_error(string preset)
    {
        var result = Load(SetupFixtures.With("\"defaultPreset\": \"free-lane\"", $"\"defaultPreset\": \"{preset}\""));

        Assert.True(HasError(result, "models.defaultPreset"));
    }

    [Fact]
    public void The_new_registry_keys_exist_at_install_scope_with_the_plan_defaults()
    {
        foreach (var name in new[] { "identity.merge_credential", "identity.merge_credential.app_id_ref", "gateway.default_preset", "company.source" })
        {
            Assert.True(RegistrySetupSettingCatalog.Known.TryGet(name, out var definition), name);
            Assert.True(definition!.IsInstallScope, name);
        }
    }

    [Theory]
    [InlineData("identity.merge_credential", "owner_app", true)]
    [InlineData("identity.merge_credential", "linked_user", true)]
    [InlineData("identity.merge_credential", "owner_linked", false)]
    [InlineData("identity.merge_credential", "pat", false)]
    [InlineData("identity.merge_credential.app_id_ref", "", true)]
    [InlineData("identity.merge_credential.app_id_ref", "secret://bazaar/owner/app-id", true)]
    [InlineData("identity.merge_credential.app_id_ref", "env:APP_ID", false)]
    [InlineData("identity.merge_credential.app_id_ref", "secret://bazaar/", false)]
    [InlineData("identity.merge_credential.app_id_ref", "secret://bazaar/a b", false)]
    [InlineData("gateway.default_preset", "free-lane", true)]
    [InlineData("gateway.default_preset", "Free Lane", false)]
    [InlineData("gateway.default_preset", "", false)]
    [InlineData("company.source", "blank", true)]
    [InlineData("company.source", "template:starter@1.0.0", true)]
    [InlineData("company.source", "import:acme-v1", true)]
    [InlineData("company.source", "template:starter", false)]
    [InlineData("company.source", "https://example.invalid/x", false)]
    [InlineData("company.source", "import:", false)]
    public void The_registry_checks_each_new_key(string key, string value, bool accepted)
    {
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet(key, out var definition));

        Assert.Equal(accepted, definition!.CheckValue(Json(value)) is null);
        Assert.Equal(accepted, definition.Evaluate("setup:plan", Json(value)).Accepted);
    }

    [Fact]
    public void The_linked_credential_projects_as_linked_user_and_no_reference()
    {
        var plan = new SetupPlan();

        var projected = SetupSettingProjection.Project(plan);

        Assert.Equal("linked_user", Assert.Single(projected, s => s.Key == "identity.merge_credential").Value.GetString());
        Assert.DoesNotContain(projected, s => s.Key == "identity.merge_credential.app_id_ref");
        Assert.Equal("free-lane", Assert.Single(projected, s => s.Key == "gateway.default_preset").Value.GetString());
        Assert.DoesNotContain(projected, s => s.Key == "company.source");
    }

    [Fact]
    public void An_owner_app_credential_projects_its_reference_and_every_projected_value_passes_the_registry()
    {
        var plan = Load(SetupFixtures.Valid).Plan!;

        var projected = SetupSettingProjection.Project(plan);

        Assert.Equal("owner_app", Assert.Single(projected, s => s.Key == "identity.merge_credential").Value.GetString());
        Assert.Equal("secret://bazaar/owner/app-id", Assert.Single(projected, s => s.Key == "identity.merge_credential.app_id_ref").Value.GetString());
        Assert.Equal("template:starter@1.0.0", Assert.Single(projected, s => s.Key == "company.source").Value.GetString());
        foreach (var setting in projected)
        {
            Assert.True(RegistrySetupSettingCatalog.Known.TryGet(setting.Key, out var definition), setting.Key);
            Assert.True(definition!.Evaluate("setup:plan", setting.Value).Accepted, setting.Key);
        }
    }

    [Fact]
    public void The_three_parts_are_requested_sections_a_step_must_cover()
    {
        var plan = Load(SetupFixtures.Valid).Plan!;

        var unapplied = SetupPlanSections.Unapplied(plan, ["install.releaseChannel", "install.updatePolicy", "network", "settings"]).Select(section => section.Id);

        Assert.Contains("models", unapplied);
        Assert.Contains("git", unapplied);
        Assert.Contains("company", unapplied);
    }
}
