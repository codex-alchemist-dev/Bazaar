using Bazaar.Client.Core.Layout;

namespace Bazaar.Client.Core.Tests.Layout;

public class LayoutValidatorTests
{
    private const string _valid = """
        {
          "schemaVersion": 1,
          "name": "Company default",
          "locks": ["phone/*/*"],
          "slots": [
            {
              "deviceClass": "phone", "display": "single", "posture": "*",
              "root": { "container": "column", "children": [
                { "paneKind": "bazaar.gc", "paneId": "gc-list" },
                { "paneKind": "bazaar.dashboard", "paneConfig": { "density": "compact" } }
              ] }
            }
          ]
        }
        """;

    [Fact]
    public void Valid_layout_loads_with_its_slots_and_locks()
    {
        LayoutLoadResult result = LayoutValidator.Validate(_valid);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
        Assert.Equal("Company default", result.Document!.Name);
        Assert.Equal(["phone/*/*"], result.Document.Locks);
        LayoutSlot slot = Assert.Single(result.Document.Slots);
        Assert.Equal(LayoutVocabulary.Any, slot.Posture);
        Assert.Equal("column", slot.Root.Container);
        Assert.Equal("gc-list", slot.Root.Children[0].PaneId);
        Assert.Equal("bazaar.dashboard", slot.Root.Children[1].PaneKind);
        Assert.NotNull(slot.Root.Children[1].PaneConfig);
    }

    [Fact]
    public void Wrong_schema_version_is_rejected_with_path()
    {
        LayoutLoadResult result = LayoutValidator.Validate(_valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "/schemaVersion" && i.Code == "unsupported-schema-version");
    }

    [Fact]
    public void Unknown_top_level_and_node_fields_are_rejected()
    {
        string json = _valid.Replace("\"locks\"", "\"script\": 1, \"locks\"").Replace("\"paneId\": \"gc-list\"", "\"paneId\": \"gc-list\", \"visibleWhen\": \"x\"");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/script" && i.Code == "unknown-field");
        Assert.Contains(result.Issues, i => i.Code == "unknown-field" && i.Path.EndsWith("/visibleWhen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://example.com/x")]
    [InlineData("<script>")]
    [InlineData("javascript:alert")]
    [InlineData("C:\\\\temp")]
    public void Forbidden_content_in_strings_is_rejected(string text)
    {
        string json = _valid.Replace("\"Company default\"", $"\"{text.Replace("\\", "\\\\")}\"");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "forbidden-content" || i.Code == "bad-name");
    }

    [Fact]
    public void Forbidden_content_in_pane_config_is_rejected()
    {
        string json = _valid.Replace("\"compact\"", "\"<img src=x>\"");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "forbidden-content" && i.Path.Contains("paneConfig"));
    }

    [Fact]
    public void Node_must_be_either_container_or_pane()
    {
        string json = _valid.Replace("{ \"paneKind\": \"bazaar.gc\", \"paneId\": \"gc-list\" }", "{ \"container\": \"row\", \"paneKind\": \"bazaar.gc\", \"children\": [] }");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "bad-node");
    }

    [Fact]
    public void Unknown_container_is_rejected_on_import()
    {
        string json = _valid.Replace("\"column\"", "\"grid\"");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "unknown-container");
    }

    [Fact]
    public void Duplicate_slot_keys_are_rejected()
    {
        string slot = """{ "deviceClass": "phone", "display": "single", "posture": "folded", "root": { "paneKind": "bazaar.gc" } }""";
        string json = $$"""{ "schemaVersion": 1, "name": "Dup", "slots": [{{slot}}, {{slot}}] }""";

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "duplicate-slot" && i.Path == "/slots/1");
    }

    [Fact]
    public void Nesting_beyond_sixteen_levels_is_rejected()
    {
        string node = "{ \"paneKind\": \"bazaar.gc\" }";
        for (int i = 0; i < 16; i++)
        {
            node = $"{{ \"container\": \"stack\", \"children\": [{node}] }}";
        }

        string json = $$"""{ "schemaVersion": 1, "name": "Deep", "slots": [{ "deviceClass": "*", "display": "*", "posture": "*", "root": {{node}} }] }""";

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "too-deep");
    }

    [Fact]
    public void Too_many_panes_in_one_slot_is_rejected()
    {
        // Three columns of 22 panes: 66 panes, each container under the child limit.
        string column = "{ \"container\": \"column\", \"children\": [" + string.Join(",", Enumerable.Range(0, 22).Select(_ => "{ \"paneKind\": \"bazaar.gc\" }")) + "] }";
        string json = $$"""{ "schemaVersion": 1, "name": "Many", "slots": [{ "deviceClass": "*", "display": "*", "posture": "*", "root": { "container": "row", "children": [{{column}}, {{column}}, {{column}}] } }] }""";

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "too-many-panes");
    }

    [Fact]
    public void Bad_pane_kind_and_bad_enum_values_are_reported()
    {
        string json = _valid.Replace("\"bazaar.gc\"", "\"Bad Kind!\"").Replace("\"phone\"", "\"watch\"");

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "bad-pane-kind");
        Assert.Contains(result.Issues, i => i.Code == "bad-value" && i.Path.EndsWith("/deviceClass", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    public void Malformed_input_never_throws(string json)
    {
        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void Oversized_input_is_rejected_without_parsing()
    {
        string json = new string(' ', LayoutVocabulary.MaxDocumentChars + 1);

        LayoutLoadResult result = LayoutValidator.Validate(json);

        Assert.Equal("too-large", Assert.Single(result.Issues).Code);
    }
}
