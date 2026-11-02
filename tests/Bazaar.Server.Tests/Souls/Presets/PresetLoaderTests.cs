using System.Text;
using Bazaar.Server.Souls.Presets;

namespace Bazaar.Server.Tests.Souls.Presets;

public sealed class PresetLoaderTests
{
    private const string _policy = """{ "route": "custom/cheap-code", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }""";

    /// <summary>One preset object as JSON text. <paramref name="extra"/> adds raw members, for example <c>, "x": 1</c>.</summary>
    private static string Preset(
        string id = "p1",
        string modelPolicy = _policy,
        string preferences = """{ "verbosity": "terse" }""",
        string extra = "",
        string description = "Writes boilerplate.") =>
        $$"""{ "id": "{{id}}", "schemaVersion": 1, "name": "Tester", "role": "r", "description": "{{description}}", "specialties": ["testing"], "modelPolicy": {{modelPolicy}}, "preferences": {{preferences}}{{extra}} }""";

    private static string Set(params string[] presets) => "[" + string.Join(",", presets) + "]";

    private static PresetValidationException Reject(string json) =>
        Assert.Throws<PresetValidationException>(() => PresetLoader.Parse(json));

    private static void AssertHas(PresetValidationException ex, PresetIssueCode code) =>
        Assert.Contains(ex.Issues, issue => issue.Code == code);

    [Fact]
    public void Valid_set_parses_into_presets()
    {
        var presets = PresetLoader.Parse(Set(Preset()));

        var preset = Assert.Single(presets);
        Assert.Equal("p1", preset.Id);
        Assert.Equal(1, preset.SchemaVersion);
        Assert.Equal("Tester", preset.Name);
        Assert.Equal(["testing"], preset.Specialties);
        Assert.Equal("custom/cheap-code", preset.ModelPolicy.Route);
        Assert.Equal(["anthropic-direct:claude-haiku-5-5"], preset.ModelPolicy.Whitelist);
        Assert.Equal("{\"verbosity\":\"terse\"}", preset.PreferencesJson);
    }

    [Fact]
    public void Stream_and_string_inputs_give_the_same_presets()
    {
        var json = Set(Preset(), Preset(id: "p2", modelPolicy: """{ "route": "anthropic-direct:claude-haiku-5-5", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }"""));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var fromStream = PresetLoader.Parse(stream);
        var fromString = PresetLoader.Parse(json);

        Assert.Equal(fromString.Select(p => p.Id), fromStream.Select(p => p.Id));
        Assert.Equal(fromString.Select(p => p.ModelPolicy.Route), fromStream.Select(p => p.ModelPolicy.Route));
    }

    [Fact]
    public void Preferences_are_canonicalized_and_default_to_an_empty_object()
    {
        var canonical = Assert.Single(PresetLoader.Parse(Set(Preset(preferences: """{ "b": 1, "a": { "y": true, "x": null } }"""))));
        Assert.Equal("{\"a\":{\"x\":null,\"y\":true},\"b\":1}", canonical.PreferencesJson);

        var withoutPreferences = Set("""{ "id": "p1", "schemaVersion": 1, "name": "T", "role": "r", "description": "d", "modelPolicy": { "route": "custom/x", "whitelist": ["anthropic-direct:claude-haiku-5-5"] } }""");
        Assert.Equal("{}", Assert.Single(PresetLoader.Parse(withoutPreferences)).PreferencesJson);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[1,")]
    public void Invalid_json_is_rejected(string json)
    {
        AssertHas(Reject(json), PresetIssueCode.InvalidJson);
    }

    [Fact]
    public void Invalid_json_on_a_stream_is_rejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"id\":"));

        AssertHas(Assert.Throws<PresetValidationException>(() => PresetLoader.Parse(stream)), PresetIssueCode.InvalidJson);
    }

    [Fact]
    public void A_root_that_is_not_an_array_is_rejected()
    {
        AssertHas(Reject(Preset()), PresetIssueCode.NotAnArray);
    }

    [Fact]
    public void Unknown_top_level_key_is_rejected()
    {
        AssertHas(Reject(Set(Preset(extra: """, "mood": "happy" """))), PresetIssueCode.UnknownKey);
    }

    [Fact]
    public void Unknown_model_policy_key_is_rejected()
    {
        var policy = """{ "route": "custom/cheap-code", "whitelist": ["anthropic-direct:claude-haiku-5-5"], "fallback": "x" }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.UnknownKey);
    }

    [Fact]
    public void Duplicate_preset_ids_are_rejected()
    {
        AssertHas(Reject(Set(Preset(id: "same"), Preset(id: "same"))), PresetIssueCode.DuplicateId);
    }

    [Fact]
    public void A_duplicate_id_is_caught_even_when_the_preset_has_other_problems()
    {
        var ex = Reject(Set(Preset(id: "same"), Preset(id: "same", extra: """, "mood": "happy" """)));

        AssertHas(ex, PresetIssueCode.DuplicateId);
        AssertHas(ex, PresetIssueCode.UnknownKey);
    }

    [Fact]
    public void Duplicate_keys_in_one_object_are_rejected()
    {
        AssertHas(Reject(Set(Preset(extra: """, "name": "Other" """))), PresetIssueCode.DuplicateKey);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("phoneNumber")]
    [InlineData("first_name")]
    [InlineData("date_of_birth")]
    [InlineData("homeAddress")]
    [InlineData("passport")]
    [InlineData("apiKey")]
    [InlineData("ssn")]
    public void Personal_data_keys_are_rejected(string key)
    {
        AssertHas(Reject(Set(Preset(extra: $$""", "{{key}}": "x" """))), PresetIssueCode.PersonalDataKey);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("projectContext")]
    [InlineData("vault")]
    [InlineData("lessons")]
    [InlineData("memory")]
    [InlineData("gcMessages")]
    [InlineData("mirrorProfile")]
    public void Project_context_and_export_excluded_keys_are_rejected(string key)
    {
        AssertHas(Reject(Set(Preset(extra: $$""", "{{key}}": "x" """))), PresetIssueCode.PersonalDataKey);
    }

    [Fact]
    public void Personal_data_keys_are_rejected_at_any_depth_including_preferences()
    {
        var ex = Reject(Set(Preset(preferences: """{ "profile": { "email": "x" } }""")));

        AssertHas(ex, PresetIssueCode.PersonalDataKey);
    }

    [Fact]
    public void A_string_that_holds_an_email_address_is_rejected()
    {
        AssertHas(Reject(Set(Preset(description: "Reach me at person@example.com"))), PresetIssueCode.PersonalDataValue);
    }

    [Theory]
    [InlineData("auto/cheap")]
    [InlineData("auto")]
    [InlineData("AUTO/cheap")]
    [InlineData("anthropic-direct:auto/best")]
    public void Global_auto_routes_are_rejected(string route)
    {
        var policy = $$"""{ "route": "{{route}}", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.AutoRoute);
    }

    [Fact]
    public void Global_auto_entries_in_a_whitelist_are_rejected()
    {
        var policy = """{ "route": "custom/cheap-code", "whitelist": ["auto/cheap"] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.AutoRoute);
    }

    [Theory]
    [InlineData("custom/*")]
    [InlineData("custom/")]
    [InlineData("*")]
    [InlineData("anthropic-direct:*")]
    [InlineData("cheap-code")]
    public void Wildcard_and_unqualified_routes_are_rejected(string route)
    {
        var policy = $$"""{ "route": "{{route}}", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.InvalidRoute);
    }

    [Fact]
    public void A_provider_qualified_route_outside_the_whitelist_is_rejected()
    {
        var policy = """{ "route": "anthropic-direct:claude-sonnet-5-5", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.RouteNotWhitelisted);
    }

    [Fact]
    public void A_provider_qualified_route_inside_the_whitelist_is_accepted()
    {
        var policy = """{ "route": "anthropic-direct:claude-sonnet-5-5", "whitelist": ["anthropic-direct:claude-haiku-5-5", "anthropic-direct:claude-sonnet-5-5"] }""";

        var preset = Assert.Single(PresetLoader.Parse(Set(Preset(modelPolicy: policy))));

        Assert.Equal("anthropic-direct:claude-sonnet-5-5", preset.ModelPolicy.Route);
    }

    [Fact]
    public void An_empty_whitelist_is_rejected()
    {
        var policy = """{ "route": "custom/cheap-code", "whitelist": [] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.EmptyWhitelist);
    }

    [Fact]
    public void A_missing_whitelist_is_rejected()
    {
        var policy = """{ "route": "custom/cheap-code" }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.MissingKey);
    }

    [Theory]
    [InlineData("claude-haiku-5-5")]
    [InlineData("custom/*")]
    [InlineData("anthropic-direct:*")]
    public void Whitelist_entries_must_be_provider_qualified_models(string entry)
    {
        var policy = $$"""{ "route": "custom/cheap-code", "whitelist": ["{{entry}}"] }""";

        AssertHas(Reject(Set(Preset(modelPolicy: policy))), PresetIssueCode.InvalidWhitelistEntry);
    }

    [Fact]
    public void An_unsupported_schema_version_is_rejected()
    {
        var json = Set("""{ "id": "p1", "schemaVersion": 2, "name": "T", "role": "r", "description": "d", "modelPolicy": { "route": "custom/x", "whitelist": ["anthropic-direct:claude-haiku-5-5"] } }""");

        AssertHas(Reject(json), PresetIssueCode.UnsupportedSchemaVersion);
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("-leading")]
    [InlineData("UPPER")]
    public void Invalid_preset_ids_are_rejected(string id)
    {
        AssertHas(Reject(Set(Preset(id: id))), PresetIssueCode.InvalidId);
    }

    [Fact]
    public void A_missing_required_key_is_rejected()
    {
        var json = Set("""{ "id": "p1", "schemaVersion": 1, "role": "r", "description": "d", "modelPolicy": { "route": "custom/x", "whitelist": ["anthropic-direct:claude-haiku-5-5"] } }""");

        AssertHas(Reject(json), PresetIssueCode.MissingKey);
    }

    [Fact]
    public void Repeated_specialties_are_rejected()
    {
        var json = Set("""{ "id": "p1", "schemaVersion": 1, "name": "T", "role": "r", "description": "d", "specialties": ["a", "a"], "modelPolicy": { "route": "custom/x", "whitelist": ["anthropic-direct:claude-haiku-5-5"] } }""");

        AssertHas(Reject(json), PresetIssueCode.InvalidValue);
    }

    [Fact]
    public void Every_problem_in_a_set_is_reported_together()
    {
        var ex = Reject(Set(Preset(id: "a", extra: """, "mood": "happy" """), Preset(id: "b", modelPolicy: """{ "route": "auto/cheap", "whitelist": ["anthropic-direct:claude-haiku-5-5"] }""")));

        AssertHas(ex, PresetIssueCode.UnknownKey);
        AssertHas(ex, PresetIssueCode.AutoRoute);
        Assert.True(ex.Issues.Count >= 2);
    }

    [Fact]
    public void Valid_preset_with_no_specialties_is_accepted()
    {
        var json = Set("""{ "id": "junior", "schemaVersion": 1, "name": "J", "role": "r", "description": "d", "specialties": [], "modelPolicy": { "route": "custom/cheap-code", "whitelist": ["anthropic-direct:claude-haiku-5-5"] } }""");

        var preset = Assert.Single(PresetLoader.Parse(json));

        Assert.Empty(preset.Specialties);
    }
}
