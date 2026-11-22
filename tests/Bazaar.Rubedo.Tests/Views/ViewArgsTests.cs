using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Tests.Views;

/// <summary>Button arguments (doc 41c, section 6.1): reading and validation.</summary>
public class ViewArgsTests
{
    private static string Json(string argsJson, string widget = "button") => $$"""
        {
          "dslVersion": 1,
          "name": "bazaar.test.args",
          "data": { "rows": { "capability": "fixture:rows", "type": "list", "fields": { "id": "text", "count": "number" } } },
          "actions": { "go": { "kind": "invoke", "target": "test.go" } },
          "root": { "widget": "list", "props": { "over": "rows", "key": "id" }, "children": [
            { "widget": "{{widget}}", "props": { "label": "Go", "action": "go", "args": {{argsJson}} } } ] }
        }
        """;

    private static string[] Codes(string json) =>
        ViewValidator.Validate(ViewDocumentReader.Read(json)).Select(d => d.Code).ToArray();

    [Fact]
    public void Literal_and_expression_arguments_validate_in_the_row_scope()
    {
        Assert.Empty(Codes(Json("""{ "id": { "expr": "id" }, "n": 2, "on": true, "t": "x" }""")));
    }

    [Fact]
    public void An_expression_that_does_not_compile_is_reported()
    {
        Assert.NotEmpty(Codes(Json("""{ "id": { "expr": "nope + 1" } }""")));
    }

    [Fact]
    public void A_nested_value_is_rejected_by_the_reader()
    {
        Assert.Throws<FormatException>(() => ViewDocumentReader.Read(Json("""{ "id": { "a": 1 } }""")));
    }

    [Fact]
    public void A_bad_argument_name_is_reported()
    {
        Assert.Contains("view.args_name", Codes(Json("""{ "bad-name": 1 }""")));
    }

    [Fact]
    public void More_than_eight_arguments_are_reported()
    {
        string many = "{" + string.Join(",", Enumerable.Range(0, 9).Select(i => $"\"a{i}\": {i}")) + "}";

        Assert.Contains("view.args_count", Codes(Json(many)));
    }

    [Fact]
    public void A_null_argument_is_reported()
    {
        Assert.Contains("view.args_value", Codes(Json("""{ "id": null }""")));
    }

    [Fact]
    public void Only_a_button_takes_args()
    {
        Assert.Contains("view.args_widget", Codes(Json("""{ "id": 1 }""", widget: "text")));
    }
}
