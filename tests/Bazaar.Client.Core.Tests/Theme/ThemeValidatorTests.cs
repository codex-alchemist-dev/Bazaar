using System.Text.Json.Nodes;
using Bazaar.Client.Core.Theme;

namespace Bazaar.Client.Core.Tests.Theme;

public class ThemeValidatorTests
{
    [Fact]
    public void Dark_fixture_is_valid_and_canonical()
    {
        ThemeLoadResult result = ThemeValidator.Validate(ThemeFixtures.Read("dark.theme.json"));

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Definition);
        Assert.Equal(ThemeBase.Dark, result.Definition.Base);
        Assert.Equal("Bazaar Dark", result.Definition.Name);
        Assert.Equal("#1E1E2E", result.Definition.Tokens["color.background.base"]);
        Assert.Equal("14", result.Definition.Tokens["font.size.body"]);
        Assert.Equal("false", result.Definition.Tokens["reducedMotion"]);
    }

    [Fact]
    public void Light_fixture_is_valid()
    {
        ThemeLoadResult result = ThemeValidator.Validate(ThemeFixtures.Read("light.theme.json"));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code} {i.Message}")));
        Assert.Equal(ThemeBase.Light, result.Definition!.Base);
    }

    [Fact]
    public void Missing_required_token_is_reported_by_path()
    {
        string json = Mutate(root => Tokens(root).Remove("space.md"));

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "/tokens/space.md" && i.Code == "missing-token");
    }

    [Fact]
    public void Unknown_token_is_rejected()
    {
        string json = Mutate(root => Tokens(root)["color.evil"] = "#000000");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/color.evil" && i.Code == "unknown-token");
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1E1E2E80x")]
    [InlineData("1E1E2E")]
    public void Malformed_colour_is_rejected(string colour)
    {
        string json = Mutate(root => Tokens(root)["color.text.primary"] = colour);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/color.text.primary" && i.Code == "bad-value");
    }

    [Fact]
    public void Alpha_colour_is_accepted_on_a_non_contrast_token()
    {
        string json = Mutate(root => Tokens(root)["color.border.default"] = "#45475a80");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.True(result.IsValid);
        Assert.Equal("#45475A80", result.Definition!.Tokens["color.border.default"]);
    }

    [Fact]
    public void Low_contrast_text_is_rejected()
    {
        string json = Mutate(root => Tokens(root)["color.text.secondary"] = "#1F1F2A");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/color.text.secondary" && i.Code == "contrast");
    }

    [Fact]
    public void Alpha_text_colour_on_a_contrast_pair_is_rejected()
    {
        string json = Mutate(root => Tokens(root)["color.text.primary"] = "#CDD6F480");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "contrast-alpha");
    }

    [Fact]
    public void Url_in_name_is_rejected()
    {
        string json = Mutate(root => root["name"] = "https://example.com/theme");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "/name" && i.Code == "unsafe-content");
    }

    [Fact]
    public void Script_markup_in_name_is_rejected()
    {
        string json = Mutate(root => root["name"] = "<script>alert(1)</script>");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "unsafe-content");
    }

    [Fact]
    public void Remote_font_url_is_rejected()
    {
        string json = Mutate(root => Tokens(root)["font.family.ui"] = "url(https://evil.example/f.woff)");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/font.family.ui" && i.Code == "unsafe-content");
        Assert.Contains(result.Issues, i => i.Path == "/tokens/font.family.ui" && i.Code == "bad-value");
    }

    [Fact]
    public void Unsafe_key_name_is_rejected()
    {
        string json = Mutate(root => root["@import"] = "x");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "unsafe-content");
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        string json = Mutate(root => root["schemaVersion"] = 2);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/schemaVersion" && i.Code == "unsupported-schema-version");
    }

    [Fact]
    public void Unknown_top_level_field_is_rejected()
    {
        string json = Mutate(root => root["extra"] = 1);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/extra" && i.Code == "unknown-field");
    }

    [Fact]
    public void Unknown_base_is_rejected()
    {
        string json = Mutate(root => root["base"] = "sepia");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/base" && i.Code == "bad-base");
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("1.5")]
    [InlineData("-1")]
    public void Length_must_be_an_integer_from_zero_to_256(string value)
    {
        string json = Mutate(root => Tokens(root)["space.md"] = JsonNode.Parse(value));

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/space.md" && i.Code == "bad-value");
    }

    [Fact]
    public void Reduced_motion_must_be_a_boolean()
    {
        string json = Mutate(root => Tokens(root)["reducedMotion"] = "no");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/reducedMotion" && i.Code == "bad-value");
    }

    [Fact]
    public void Duplicate_key_is_rejected()
    {
        string json = ThemeFixtures.Read("dark.theme.json").Replace(
            "\"name\": \"Bazaar Dark\",",
            "\"name\": \"Bazaar Dark\", \"name\": \"Other\",",
            StringComparison.Ordinal);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "duplicate-key");
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        ThemeLoadResult result = ThemeValidator.Validate("{");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "json-syntax");
    }

    [Fact]
    public void Non_object_root_is_rejected()
    {
        ThemeLoadResult result = ThemeValidator.Validate("[]");

        Assert.Contains(result.Issues, i => i.Code == "not-object");
    }

    private static string Mutate(Action<JsonObject> change)
    {
        JsonObject root = JsonNode.Parse(ThemeFixtures.Read("dark.theme.json"))!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static JsonObject Tokens(JsonObject root) => root["tokens"]!.AsObject();
}
