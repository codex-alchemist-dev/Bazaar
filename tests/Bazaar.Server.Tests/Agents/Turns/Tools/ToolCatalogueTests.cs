using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.Server.Tests.Agents.Turns.Tools;

/// <summary>The catalogue of tool definitions a turn may offer a model (doc 06, Tool dispatch).</summary>
public sealed class ToolCatalogueTests
{
    private const string _schema = """{"type":"object","properties":{}}""";

    [Fact]
    public void No_definitions_give_an_empty_catalogue()
    {
        Assert.Empty(ToolCatalogue.Build([], Registry()));
    }

    [Fact]
    public void Definitions_are_ordered_by_name_whatever_the_registration_order()
    {
        var registry = Registry("b.tool", "a.tool");

        var catalogue = ToolCatalogue.Build([Definition("b.tool"), Definition("a.tool")], registry);

        Assert.Equal(["a.tool", "b.tool"], catalogue.Select(d => d.Name));
    }

    [Fact]
    public void A_registered_tool_without_a_definition_is_simply_not_in_the_catalogue()
    {
        var catalogue = ToolCatalogue.Build([Definition("a.tool")], Registry("a.tool", "hidden.tool"));

        Assert.Equal("a.tool", Assert.Single(catalogue).Name);
    }

    [Fact]
    public void A_definition_without_a_registered_tool_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalogue.Build([Definition("ghost.tool")], Registry()));

        Assert.Contains("ghost.tool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_name_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ToolCatalogue.Build([Definition("a.tool"), Definition("a.tool")], Registry("a.tool")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"properties":{}}""")]
    [InlineData("""{"type":"string"}""")]
    public void A_schema_that_is_not_an_object_schema_is_refused(string schema)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ToolCatalogue.Build([new ModelToolDefinition("a.tool", "Does a thing.", schema)], Registry("a.tool")));
    }

    [Fact]
    public void A_blank_description_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ToolCatalogue.Build([new ModelToolDefinition("a.tool", " ", _schema)], Registry("a.tool")));
    }

    private static ModelToolDefinition Definition(string name) => new(name, "Does a thing.", _schema);

    private static ToolRegistry Registry(params string[] names) => new(names.Select(n => (n, (ITool)new NoTool())));

    private sealed class NoTool : ITool
    {
        public Task<string> ExecuteAsync(JsonElement args, CancellationToken cancellationToken) => Task.FromResult("{}");
    }
}
