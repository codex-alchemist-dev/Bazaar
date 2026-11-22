using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Rubedo.LanguageServer;

namespace Bazaar.Rubedo.LanguageServer.Tests;

public sealed class LanguageServerTests
{
    private const string Uri = "file:///a.rubedo";
    private const string ViewUri = "file:///v.json";

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static List<JsonObject> Send(LanguageServerCore core, string json) =>
        core.Process(Bytes(json)).Select(s => JsonNode.Parse(s)!.AsObject()).ToList();

    private static LanguageServerCore Started()
    {
        var core = new LanguageServerCore();
        Send(core, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Send(core, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        return core;
    }

    private static List<JsonObject> Open(LanguageServerCore core, string uri, string languageId, string text)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = languageId, ["version"] = 1, ["text"] = text },
            },
        };
        return Send(core, message.ToJsonString());
    }

    private static JsonObject Request(LanguageServerCore core, string method, string uri, int line = 0, int character = 0, int id = 7)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
            },
        };
        string json = message.ToJsonString();
        return Assert.Single(Send(core, json));
    }

    [Fact]
    public void Initialize_advertises_capabilities_and_dsl_version()
    {
        var core = new LanguageServerCore();
        JsonObject reply = Assert.Single(Send(core, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""));
        JsonNode caps = reply["result"]!["capabilities"]!;
        Assert.Equal("utf-16", (string?)caps["positionEncoding"]);
        Assert.Equal(1, (int?)caps["textDocumentSync"]!["change"]);
        Assert.True((bool?)caps["hoverProvider"]);
        Assert.True((bool?)caps["documentFormattingProvider"]);
        Assert.True((bool?)caps["documentSymbolProvider"]);
        Assert.NotNull(caps["completionProvider"]);
        Assert.NotNull(caps["signatureHelpProvider"]);
        Assert.Equal(RubedoLanguage.Version, (int?)reply["result"]!["experimental"]!["dslVersion"]);
    }

    [Fact]
    public void Request_before_initialize_is_refused()
    {
        var core = new LanguageServerCore();
        JsonObject reply = Assert.Single(Send(core, """{"jsonrpc":"2.0","id":3,"method":"textDocument/hover","params":{}}"""));
        Assert.Equal(LspErrorCodes.ServerNotInitialized, (int?)reply["error"]!["code"]);
        Assert.Equal(3, (int?)reply["id"]);
    }

    [Fact]
    public void Second_initialize_is_an_invalid_request()
    {
        LanguageServerCore core = Started();
        JsonObject reply = Assert.Single(Send(core, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}"""));
        Assert.Equal(LspErrorCodes.InvalidRequest, (int?)reply["error"]!["code"]);
    }

    [Fact]
    public void Unknown_request_is_method_not_found_and_unknown_notification_is_ignored()
    {
        LanguageServerCore core = Started();
        JsonObject reply = Assert.Single(Send(core, """{"jsonrpc":"2.0","id":"abc","method":"workspace/nope"}"""));
        Assert.Equal(LspErrorCodes.MethodNotFound, (int?)reply["error"]!["code"]);
        Assert.Equal("abc", (string?)reply["id"]);
        Assert.Empty(Send(core, """{"jsonrpc":"2.0","method":"workspace/nope"}"""));
    }

    [Fact]
    public void Malformed_json_is_a_parse_error_with_null_id()
    {
        LanguageServerCore core = Started();
        JsonObject reply = Assert.Single(Send(core, "{not json"));
        Assert.Equal(LspErrorCodes.ParseError, (int?)reply["error"]!["code"]);
        Assert.Null(reply["id"]);
    }

    [Fact]
    public void Non_object_and_bad_id_are_invalid_requests()
    {
        LanguageServerCore core = Started();
        Assert.Equal(LspErrorCodes.InvalidRequest, (int?)Assert.Single(Send(core, "[1,2]"))["error"]!["code"]);
        Assert.Equal(LspErrorCodes.InvalidRequest, (int?)Assert.Single(Send(core, """{"jsonrpc":"2.0","id":{"x":1},"method":"shutdown"}"""))["error"]!["code"]);
        Assert.Equal(LspErrorCodes.InvalidRequest, (int?)Assert.Single(Send(core, """{"jsonrpc":"2.0","id":5}"""))["error"]!["code"]);
    }

    [Fact]
    public void Open_publishes_diagnostics_with_line_and_column()
    {
        LanguageServerCore core = Started();
        List<JsonObject> replies = Open(core, Uri, "rubedo", "1 +\n  )");
        JsonObject note = Assert.Single(replies);
        Assert.Equal("textDocument/publishDiagnostics", (string?)note["method"]);
        JsonArray diagnostics = note["params"]!["diagnostics"]!.AsArray();
        Assert.NotEmpty(diagnostics);
        JsonNode first = diagnostics[0]!;
        Assert.Equal(1, (int?)first["severity"]);
        Assert.False(string.IsNullOrEmpty((string?)first["code"]));
        Assert.Equal(1, (int?)note["params"]!["version"]);
        Assert.True((int?)first["range"]!["start"]!["line"] >= 0);
        Assert.Contains(diagnostics, d => (int?)d!["range"]!["start"]!["line"] == 1);
    }

    [Fact]
    public void Valid_expression_publishes_empty_diagnostics()
    {
        LanguageServerCore core = Started();
        JsonObject note = Assert.Single(Open(core, Uri, "rubedo", "1 + 2"));
        Assert.Empty(note["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Change_replaces_the_text_and_republishes_and_close_clears()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "1 +");
        JsonObject changed = Assert.Single(Send(core, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///a.rubedo","version":2},"contentChanges":[{"text":"1 + 2"}]}}"""));
        Assert.Empty(changed["params"]!["diagnostics"]!.AsArray());
        Assert.Equal(2, (int?)changed["params"]!["version"]);
        JsonObject closed = Assert.Single(Send(core, """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///a.rubedo"}}}"""));
        Assert.Empty(closed["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Incremental_change_is_applied_when_a_client_sends_one()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "1 + ");
        JsonObject changed = Assert.Single(Send(core, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///a.rubedo","version":2},"contentChanges":[{"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":4}},"text":"2"}]}}"""));
        Assert.Empty(changed["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Unsupported_language_is_ignored()
    {
        LanguageServerCore core = Started();
        Assert.Empty(Open(core, "file:///x.txt", "plaintext", "hello"));
    }

    [Fact]
    public void View_diagnostics_use_the_view_validator()
    {
        LanguageServerCore core = Started();
        JsonObject note = Assert.Single(Open(core, ViewUri, "rubedo-view", "{ not json"));
        Assert.Equal("view.json", (string?)note["params"]!["diagnostics"]![0]!["code"]);
    }

    [Fact]
    public void Completion_returns_items_with_a_replace_edit()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "ab");
        JsonObject reply = Request(core, "textDocument/completion", Uri, 0, 2);
        JsonArray items = reply["result"]!["items"]!.AsArray();
        Assert.NotEmpty(items);
        Assert.NotNull(items[0]!["textEdit"]!["range"]);
        Assert.False(string.IsNullOrEmpty((string?)items[0]!["label"]));
    }

    [Fact]
    public void Completion_in_a_view_offers_widget_names()
    {
        LanguageServerCore core = Started();
        Open(core, ViewUri, "rubedo-view", "{\"root\":{\"widget\":\"\"}}");
        JsonObject reply = Request(core, "textDocument/completion", ViewUri, 0, 19);
        Assert.NotEmpty(reply["result"]!["items"]!.AsArray());
    }

    [Fact]
    public void Hover_returns_text_and_range_or_null()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "max(1, 2)");
        JsonObject hit = Request(core, "textDocument/hover", Uri, 0, 1);
        Assert.False(string.IsNullOrEmpty((string?)hit["result"]!["contents"]!["value"]));
        Assert.NotNull(hit["result"]!["range"]);

        Open(core, ViewUri, "rubedo-view", "{}");
        Assert.Null(Request(core, "textDocument/hover", ViewUri).AsObject()["result"]);
        Assert.Null(Request(core, "textDocument/hover", "file:///unknown").AsObject()["result"]);
    }

    [Fact]
    public void Signature_help_reports_the_active_parameter()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "max(1, ");
        JsonObject reply = Request(core, "textDocument/signatureHelp", Uri, 0, 7);
        JsonNode result = reply["result"]!;
        Assert.Equal(1, (int?)result["activeParameter"]);
        Assert.NotEmpty(result["signatures"]![0]!["parameters"]!.AsArray());
    }

    [Fact]
    public void Formatting_returns_one_full_edit_for_rubedo_and_null_for_views()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "1+2");
        JsonObject reply = Request(core, "textDocument/formatting", Uri);
        JsonNode edit = reply["result"]!.AsArray().Single()!;
        Assert.Equal("1 + 2", (string?)edit["newText"]);
        Assert.Equal(0, (int?)edit["range"]!["start"]!["character"]);
        Assert.Equal(3, (int?)edit["range"]!["end"]!["character"]);

        Open(core, ViewUri, "rubedo-view", "{}");
        Assert.Null(Request(core, "textDocument/formatting", ViewUri)["result"]);
    }

    [Fact]
    public void Formatting_already_canonical_or_invalid_source_gives_no_edits()
    {
        LanguageServerCore core = Started();
        Open(core, Uri, "rubedo", "1 + 2");
        Assert.Empty(Request(core, "textDocument/formatting", Uri)["result"]!.AsArray());
        Open(core, Uri, "rubedo", "1 +");
        Assert.Empty(Request(core, "textDocument/formatting", Uri)["result"]!.AsArray());
    }

    [Fact]
    public void Document_symbols_nest_widgets_for_views_and_are_empty_for_expressions()
    {
        LanguageServerCore core = Started();
        string view = "{\n\"dslVersion\":1,\"name\":\"v\",\n\"root\":{\"widget\":\"column\",\"children\":[{\"widget\":\"text\"},{\"widget\":\"row\",\"children\":[{\"widget\":\"text\"}]}]}}";
        Open(core, ViewUri, "rubedo-view", view);
        JsonArray symbols = Request(core, "textDocument/documentSymbol", ViewUri)["result"]!.AsArray();
        JsonNode root = Assert.Single(symbols)!;
        Assert.Equal("column", (string?)root["name"]);
        Assert.Equal(2, root["children"]!.AsArray().Count);
        Assert.Equal("row", (string?)root["children"]![1]!["name"]);
        Assert.Single(root["children"]![1]!["children"]!.AsArray());
        Assert.Equal(2, (int?)root["range"]!["start"]!["line"]);

        Open(core, Uri, "rubedo", "1");
        Assert.Empty(Request(core, "textDocument/documentSymbol", Uri)["result"]!.AsArray());
        Open(core, ViewUri, "rubedo-view", "{ broken");
        Assert.Empty(Request(core, "textDocument/documentSymbol", ViewUri)["result"]!.AsArray());
    }

    [Fact]
    public void Missing_position_is_invalid_params()
    {
        LanguageServerCore core = Started();
        JsonObject reply = Assert.Single(Send(core, """{"jsonrpc":"2.0","id":4,"method":"textDocument/hover","params":{"textDocument":{"uri":"x"}}}"""));
        Assert.Equal(LspErrorCodes.InvalidParams, (int?)reply["error"]!["code"]);
    }

    [Fact]
    public void Document_over_the_cap_gets_diagnostics_not_an_exception()
    {
        LanguageServerCore core = Started();
        string big = new('1', 70 * 1024);
        JsonObject note = Assert.Single(Open(core, Uri, "rubedo", big));
        Assert.NotEmpty(note["params"]!["diagnostics"]!.AsArray());
        Assert.NotNull(Request(core, "textDocument/hover", Uri, 0, 5).AsObject());
        Assert.NotNull(Request(core, "textDocument/completion", Uri, 0, 5).AsObject());
        Assert.Null(Request(core, "textDocument/formatting", Uri)["result"]);

        JsonObject viewNote = Assert.Single(Open(core, ViewUri, "rubedo-view", "{\"root\":\"" + big + "\"}"));
        Assert.NotEmpty(viewNote["params"]!["diagnostics"]!.AsArray());
        Assert.Empty(Request(core, "textDocument/documentSymbol", ViewUri)["result"]!.AsArray());
    }

    [Fact]
    public void Positions_count_utf16_units_across_line_breaks()
    {
        string text = "a\r\nbé\n😀x\rz";
        Assert.Equal(new LspPosition(1, 2), TextPositions.ToPosition(text, 5));
        Assert.Equal(new LspPosition(2, 3), TextPositions.ToPosition(text, 9));
        Assert.Equal(new LspPosition(3, 1), TextPositions.ToPosition(text, text.Length));
        Assert.Equal(5, TextPositions.ToOffset(text, 1, 2));
        Assert.Equal(text.Length, TextPositions.ToOffset(text, 9, 0));
        Assert.Equal(3, TextPositions.ToOffset(text, 1, 0));
        Assert.Equal(4, TextPositions.ToOffset(text, 1, 1));
        Assert.Equal(5, TextPositions.ToOffset(text, 1, 99));
    }

    [Fact]
    public void Shutdown_then_exit_is_code_zero_and_exit_alone_is_one()
    {
        LanguageServerCore clean = Started();
        JsonObject reply = Assert.Single(Send(clean, """{"jsonrpc":"2.0","id":9,"method":"shutdown"}"""));
        Assert.True(reply.ContainsKey("result"));
        Assert.Null(reply["result"]);
        Assert.False(clean.ExitRequested);

        JsonObject after = Assert.Single(Send(clean, """{"jsonrpc":"2.0","id":10,"method":"textDocument/hover","params":{}}"""));
        Assert.Equal(LspErrorCodes.InvalidRequest, (int?)after["error"]!["code"]);
        Assert.Empty(Open(clean, Uri, "rubedo", "1"));

        Send(clean, """{"jsonrpc":"2.0","method":"exit"}""");
        Assert.True(clean.ExitRequested);
        Assert.Equal(0, clean.ExitCode);

        LanguageServerCore abrupt = Started();
        Send(abrupt, """{"jsonrpc":"2.0","method":"exit"}""");
        Assert.True(abrupt.ExitRequested);
        Assert.Equal(1, abrupt.ExitCode);
    }

    [Fact]
    public void Document_count_is_bounded()
    {
        LanguageServerCore core = Started();
        for (int i = 0; i < LanguageServerCore.MaxOpenDocuments; i++)
        {
            Assert.Single(Open(core, $"file:///{i}", "rubedo", "1"));
        }

        Assert.Empty(Open(core, "file:///extra", "rubedo", "1"));
        Assert.Single(Open(core, "file:///0", "rubedo", "2"));
    }
}
