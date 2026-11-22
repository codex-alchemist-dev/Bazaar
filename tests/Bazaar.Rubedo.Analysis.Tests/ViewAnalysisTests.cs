namespace Bazaar.Rubedo.Analysis.Tests;

public class ViewAnalysisTests
{
    private const string _valid = """
        {
          "dslVersion": 1,
          "name": "bazaar.demo.view",
          "data": { "count": { "capability": "demo.count", "type": "number" } },
          "actions": { "openIt": { "kind": "navigate", "target": "gc" }, "other": { "kind": "invoke", "target": "x" } },
          "root": {
            "widget": "stack",
            "children": [
              { "widget": "text", "props": { "value": { "expr": "count" } } },
              { "widget": "button", "props": { "label": "demo.open", "action": "openIt" } }
            ]
          }
        }
        """;

    [Fact]
    public void Valid_document_has_no_diagnostics()
    {
        Assert.Empty(RubedoAnalyzer.DiagnoseView(_valid));
    }

    [Fact]
    public void Json_syntax_error_is_placed_at_its_position()
    {
        string json = "{\n  \"dslVersion\": 1,\n  \"name\": ,\n}";
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.Equal("view.json", diagnostic.Code);
        Assert.Equal(json.IndexOf(',', json.IndexOf("\"name\"", StringComparison.Ordinal)), diagnostic.Start);
        Assert.Equal(diagnostic.Start + 1, diagnostic.End);
    }

    [Fact]
    public void Json_error_position_counts_bytes_not_characters()
    {
        string json = "{\n  \"name\": \"éé€\", ]";
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.Equal("view.json", diagnostic.Code);
        Assert.Equal(json.IndexOf(']', StringComparison.Ordinal), diagnostic.Start);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"dslVersion\":\"1\",\"name\":\"a.b\",\"root\":{\"widget\":\"stack\"}}")]
    [InlineData("{\"dslVersion\":1,\"name\":\"a.b\",\"root\":{\"widget\":\"stack\",\"props\":{\"x\":[1]}}}")]
    [InlineData("{\"dslVersion\":1,\"name\":\"a.b\",\"actions\":{\"a\":{\"kind\":\"bogus\"}},\"root\":{\"widget\":\"stack\"}}")]
    [InlineData("")]
    [InlineData("   ")]
    public void Shape_problems_are_diagnostics(string json)
    {
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.StartsWith("view.", diagnostic.Code);
        Assert.InRange(diagnostic.Start, 0, json.Length);
        Assert.InRange(diagnostic.End, diagnostic.Start, json.Length);
    }

    [Fact]
    public void Validator_problems_are_placed_on_the_widget_of_the_node()
    {
        string json = _valid.Replace("\"widget\": \"text\"", "\"widget\": \"bogus\"", StringComparison.Ordinal);
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.Equal("view.unknown_widget", diagnostic.Code);
        Assert.Equal("\"bogus\"", json[diagnostic.Start..diagnostic.End]);
    }

    [Fact]
    public void Root_level_problems_are_placed_on_the_root_widget()
    {
        string json = _valid.Replace("\"widget\": \"stack\"", "\"widget\": \"nope\"", StringComparison.Ordinal);
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.Equal("\"nope\"", json[diagnostic.Start..diagnostic.End]);
    }

    [Fact]
    public void Expression_and_label_problems_use_the_validator_codes()
    {
        string json = _valid
            .Replace("\"expr\": \"count\"", "\"expr\": \"missing > 1\"", StringComparison.Ordinal)
            .Replace("\"label\": \"demo.open\", ", string.Empty, StringComparison.Ordinal);
        IReadOnlyList<AnalysisDiagnostic> diagnostics = RubedoAnalyzer.DiagnoseView(json);
        Assert.Equal(["unknown-identifier", "view.missing_label"], diagnostics.Select(d => d.Code).Order(StringComparer.Ordinal));
        AnalysisDiagnostic label = diagnostics.Single(d => d.Code == "view.missing_label");
        Assert.Equal("\"button\"", json[label.Start..label.End]);
    }

    [Fact]
    public void Version_and_name_problems_are_placed_on_their_values()
    {
        string json = _valid.Replace("\"dslVersion\": 1", "\"dslVersion\": 7", StringComparison.Ordinal).Replace("bazaar.demo.view", "Bad Name", StringComparison.Ordinal);
        IReadOnlyList<AnalysisDiagnostic> diagnostics = RubedoAnalyzer.DiagnoseView(json);
        AnalysisDiagnostic version = diagnostics.Single(d => d.Code == "view.dsl_version");
        Assert.Equal("7", json[version.Start..version.End]);
        AnalysisDiagnostic name = diagnostics.Single(d => d.Code == "view.bad_name");
        Assert.Equal("\"Bad Name\"", json[name.Start..name.End]);
    }

    [Fact]
    public void Positions_are_character_offsets_after_non_ascii_text()
    {
        string json = _valid.Replace("\"name\": \"bazaar.demo.view\"", "\"name\": \"bazaar.demo.view\", \"note\": \"é€\U0001F600\"", StringComparison.Ordinal)
            .Replace("\"widget\": \"text\"", "\"widget\": \"bogus\"", StringComparison.Ordinal);
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.DiagnoseView(json));
        Assert.Equal("\"bogus\"", json[diagnostic.Start..diagnostic.End]);
    }

    [Fact]
    public void Deeply_nested_json_is_a_diagnostic_not_an_exception()
    {
        string json = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        Assert.Equal("view.json", Assert.Single(RubedoAnalyzer.DiagnoseView(json)).Code);
    }

    // ---- CompleteView ----

    private static CompletionList At(string template)
    {
        int cursor = template.IndexOf('|', StringComparison.Ordinal);
        return RubedoAnalyzer.CompleteView(template.Remove(cursor, 1), cursor);
    }

    [Fact]
    public void Widget_value_completes_the_closed_set()
    {
        CompletionList list = At("""{ "root": { "widget": "|" } }""");
        Assert.Equal(13, list.Items.Count);
        Assert.All(list.Items, i => Assert.Equal(CompletionKind.Widget, i.Kind));
        Assert.Contains(list.Items, i => i.Label == "tabs");
        Assert.DoesNotContain(list.Items, i => i.Label == "tab");
    }

    [Fact]
    public void Widget_prefix_narrows_and_sets_the_replace_range()
    {
        string template = """{ "root": { "widget": "bu|" } }""";
        CompletionList list = At(template);
        Assert.Equal(["button"], list.Items.Select(i => i.Label));
        int cursor = template.IndexOf('|', StringComparison.Ordinal);
        Assert.Equal(cursor - 2, list.ReplaceStart);
        Assert.Equal(cursor, list.ReplaceEnd);
    }

    [Fact]
    public void Tab_is_offered_only_under_tabs()
    {
        CompletionList under = At("""{ "root": { "widget": "tabs", "children": [ { "widget": "ta|" } ] } }""");
        Assert.Equal(["tab", "tabs"], under.Items.Select(i => i.Label));
        CompletionList elsewhere = At("""{ "root": { "widget": "stack", "children": [ { "widget": "ta|" } ] } }""");
        Assert.Equal(["tabs"], elsewhere.Items.Select(i => i.Label));
    }

    [Fact]
    public void Prop_names_follow_the_widget()
    {
        CompletionList button = At("""{ "root": { "widget": "button", "props": { "|" } } }""");
        Assert.Equal(["action", "enabledWhen", "focusOrder", "label", "visibleWhen"], button.Items.Select(i => i.Label).Order(StringComparer.Ordinal));
        Assert.All(button.Items, i => Assert.Equal(CompletionKind.Property, i.Kind));

        CompletionList list = At("""{ "root": { "widget": "list", "props": { "o|" } } }""");
        Assert.Equal(["over"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void Prop_names_without_a_known_widget_offer_every_prop()
    {
        CompletionList list = At("""{ "root": { "props": { "|" } } }""");
        Assert.Contains(list.Items, i => i.Label == "action");
        Assert.Contains(list.Items, i => i.Label == "title");
        Assert.Contains(list.Items, i => i.Label == "visibleWhen");
    }

    [Fact]
    public void Action_ids_complete_in_a_button_action()
    {
        CompletionList list = At("""
            { "actions": { "openIt": { "kind": "navigate" }, "other": { "kind": "invoke" } },
              "root": { "widget": "button", "props": { "action": "|" } } }
            """);
        Assert.Equal(["openIt", "other"], list.Items.Select(i => i.Label));
        Assert.All(list.Items, i => Assert.Equal(CompletionKind.Action, i.Kind));
    }

    [Fact]
    public void Action_ids_declared_after_the_cursor_are_found()
    {
        CompletionList list = At("""
            { "root": { "widget": "button", "props": { "action": "|" } },
              "actions": { "late": { "kind": "navigate" } } }
            """);
        Assert.Equal(["late"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void Action_kinds_data_types_and_document_keys()
    {
        Assert.Equal(["invoke", "navigate", "openPane", "setSetting"], At("""{ "actions": { "a": { "kind": "|" } } }""").Items.Select(i => i.Label));
        Assert.Equal(["bool", "list", "number", "text"], At("""{ "data": { "a": { "type": "|" } } }""").Items.Select(i => i.Label));
        Assert.Equal(["bool", "number", "text"], At("""{ "data": { "a": { "type": "list", "fields": { "id": "|" } } } }""").Items.Select(i => i.Label));
        Assert.Equal(["actions", "data", "dslVersion", "name", "root"], At("""{ "|" }""").Items.Select(i => i.Label));
        Assert.Equal(["children", "props", "widget"], At("""{ "root": { "|" } }""").Items.Select(i => i.Label));
        Assert.Equal(["kind", "target"], At("""{ "actions": { "a": { "|" } } }""").Items.Select(i => i.Label));
    }

    [Fact]
    public void Completion_outside_a_string_or_in_other_values_is_empty()
    {
        string json = """{ "root": { "widget": "stack" } }""";
        Assert.Empty(RubedoAnalyzer.CompleteView(json, 0).Items);
        Assert.Empty(RubedoAnalyzer.CompleteView(json, json.Length).Items);
        Assert.Empty(At("""{ "name": "|" }""").Items);
        Assert.Empty(At("""[ "|" ]""").Items);
        Assert.Empty(RubedoAnalyzer.CompleteView(null, 0).Items);
        Assert.Empty(RubedoAnalyzer.CompleteView(string.Empty, 5).Items);
    }

    [Fact]
    public void Completion_works_on_unterminated_strings_and_broken_json()
    {
        Assert.Equal(["button"], RubedoAnalyzer.CompleteView("{ \"root\": { \"widget\": \"bu", 25).Items.Select(i => i.Label));
        Assert.Empty(RubedoAnalyzer.CompleteView("}}]],,::\"\"\"", 8).Items);
    }
}
