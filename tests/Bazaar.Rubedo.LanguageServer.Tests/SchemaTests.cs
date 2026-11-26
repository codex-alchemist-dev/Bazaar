using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Rubedo.LanguageServer;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.LanguageServer.Tests;

public sealed class SchemaTests
{
    private const string _uri = "file:///a.rubedo";
    private const string _schemaJson = """{"unreadCount":"number","isMuted":"bool","roomName":"text"}""";

    private static LanguageServerCore Started(RubedoSchema? schema)
    {
        var core = new LanguageServerCore(schema);
        core.Process("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""u8);
        return core;
    }

    private static JsonObject Single(IReadOnlyList<string> replies) => JsonNode.Parse(Assert.Single(replies))!.AsObject();

    private static JsonObject Open(LanguageServerCore core, string text) => Single(core.Process(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "textDocument/didOpen",
        ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = _uri, ["languageId"] = "rubedo", ["version"] = 1, ["text"] = text } },
    }.ToJsonString())));

    private static JsonObject Call(LanguageServerCore core, string method, int character) => Single(core.Process(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 5,
        ["method"] = method,
        ["params"] = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = _uri },
            ["position"] = new JsonObject { ["line"] = 0, ["character"] = character },
        },
    }.ToJsonString())));

    private static RubedoSchema Custom()
    {
        Assert.True(ServerOptions.TryParseSchema(_schemaJson, out RubedoSchema? schema, out _));
        return schema!;
    }

    [Fact]
    public void Custom_identifier_is_an_error_by_default_and_valid_with_the_schema()
    {
        Assert.NotEmpty(Open(Started(null), "unreadCount > 3")["params"]!["diagnostics"]!.AsArray());
        Assert.Empty(Open(Started(Custom()), "unreadCount > 3")["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Custom_identifier_is_completed_and_hovered()
    {
        LanguageServerCore core = Started(Custom());
        Open(core, "unr");
        JsonArray items = Call(core, "textDocument/completion", 3)["result"]!["items"]!.AsArray();
        Assert.Contains(items, i => (string?)i!["label"] == "unreadCount");
        Assert.NotNull(Call(core, "textDocument/hover", 1)["result"]);
    }

    [Fact]
    public void Default_schema_still_knows_standard_identifiers_and_formatting_uses_the_schema()
    {
        Assert.Empty(Open(Started(null), "isOwner")["params"]!["diagnostics"]!.AsArray());
        LanguageServerCore core = Started(Custom());
        Open(core, "unreadCount>3");
        JsonNode edit = Call(core, "textDocument/formatting", 0)["result"]!.AsArray().Single()!;
        Assert.Equal("unreadCount > 3", (string?)edit["newText"]);
    }

    [Fact]
    public void Schema_file_is_parsed_with_all_three_types()
    {
        Assert.True(ServerOptions.TryParseSchema(_schemaJson, out RubedoSchema? schema, out string? problem));
        Assert.Null(problem);
        Assert.Equal(RubedoType.Number, schema!.Identifiers["unreadCount"]);
        Assert.Equal(RubedoType.Bool, schema.Identifiers["isMuted"]);
        Assert.Equal(RubedoType.Text, schema.Identifiers["roomName"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"a":"integer"}""")]
    [InlineData("""{"a":3}""")]
    [InlineData("""{"1bad":"text"}""")]
    [InlineData("""{"":"text"}""")]
    public void Invalid_schema_content_is_rejected(string json)
    {
        Assert.False(ServerOptions.TryParseSchema(json, out RubedoSchema? schema, out string? problem));
        Assert.Null(schema);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Startup_arguments_report_problems_and_default_has_no_schema()
    {
        Assert.True(ServerOptions.TryParse([], out ServerOptions none, out _));
        Assert.Null(none.Schema);
        Assert.False(none.Socket);
        Assert.True(ServerOptions.TryParse(["--socket"], out ServerOptions socket, out _));
        Assert.True(socket.Socket);

        Assert.False(ServerOptions.TryParse(["--schema"], out _, out string? missing));
        Assert.NotNull(missing);
        Assert.False(ServerOptions.TryParse(["--schema", "/nonexistent/schema.json"], out _, out string? unreadable));
        Assert.Contains("Cannot read", unreadable, StringComparison.Ordinal);
        Assert.False(ServerOptions.TryParse(["--bogus"], out _, out _));
    }

    [Fact]
    public void Schema_file_on_disk_loads_and_a_bad_one_fails()
    {
        string good = Path.GetTempFileName();
        string bad = Path.GetTempFileName();
        try
        {
            File.WriteAllText(good, _schemaJson);
            File.WriteAllText(bad, """{"a":"nope"}""");
            Assert.True(ServerOptions.TryParse(["--schema", good], out ServerOptions loaded, out _));
            Assert.NotNull(loaded.Schema);
            Assert.False(ServerOptions.TryParse(["--schema", bad], out _, out string? problem));
            Assert.Contains("unknown type", problem, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(good);
            File.Delete(bad);
        }
    }
}
