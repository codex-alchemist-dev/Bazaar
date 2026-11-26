using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Rubedo.Analysis;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>
/// The transport-free language server (doc 41d section 5). Takes one JSON-RPC message body at a time and returns the messages to
/// send back (responses and published diagnostics). Holds the text the client sent and nothing else: it never reads files,
/// opens no network connection and executes nothing from a view document. Not thread safe; one connection drives one instance.
/// </summary>
public sealed class LanguageServerCore(RubedoSchema? schema = null)
{
    public const string RubedoLanguageId = "rubedo";
    public const string ViewLanguageId = "rubedo-view";
    public const int MaxOpenDocuments = 256;

    private sealed class Document(string languageId, string text, int? version)
    {
        public string LanguageId { get; } = languageId;

        public string Text { get; set; } = text;

        public int? Version { get; set; } = version;

        public bool IsView => LanguageId == ViewLanguageId;
    }

    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    private readonly RubedoSchema _schema = schema ?? RubedoSchema.Standard;
    private bool _initialized;
    private bool _shutdown;

    /// <summary>True once <c>exit</c> arrived; the transport should stop.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>0 when <c>exit</c> followed <c>shutdown</c>, else 1 (LSP section on exit).</summary>
    public int ExitCode => _shutdown ? 0 : 1;

    /// <summary>The response for bytes that could not be framed or parsed.</summary>
    public static IReadOnlyList<string> ParseError(string message) => [Error(null, LspErrorCodes.ParseError, message)];

    /// <summary>Handles one message body. Never throws for any input.</summary>
    public IReadOnlyList<string> Process(ReadOnlySpan<byte> body)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            return ParseError(ex.Message);
        }

        if (message is not JsonObject request)
        {
            return [Error(null, LspErrorCodes.InvalidRequest, "A message must be a JSON object.")];
        }

        try
        {
            return Dispatch(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lazily parsed nodes can throw late (for example on invalid UTF-8 inside a string), so even reading the id is guarded.
            return [Error(SafeIdOf(request), LspErrorCodes.InvalidParams, "The request was not understood.")];
        }
    }

    private static JsonNode? SafeIdOf(JsonObject request)
    {
        try
        {
            return IdOf(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private List<string> Dispatch(JsonObject request)
    {
        var output = new List<string>();
        if (request["method"] is not JsonValue methodValue || !methodValue.TryGetValue(out string? method))
        {
            // A response from the client (no method) is not something this server waits for.
            if (!request.ContainsKey("method") && (request.ContainsKey("result") || request.ContainsKey("error")))
            {
                return output;
            }

            output.Add(Error(request.ContainsKey("id") ? IdOf(request) : null, LspErrorCodes.InvalidRequest, "Missing method."));
            return output;
        }

        bool isRequest = request.ContainsKey("id");
        JsonNode? id = isRequest ? IdOf(request) : null;
        if (isRequest && id is null)
        {
            output.Add(Error(null, LspErrorCodes.InvalidRequest, "A request id must be a string or a number."));
            return output;
        }

        JsonNode? parameters = request["params"];
        if (method == "exit")
        {
            ExitRequested = true;
            return output;
        }

        if (isRequest)
        {
            output.Add(HandleRequest(id!, method, parameters));
        }
        else
        {
            HandleNotification(method, parameters, output);
        }

        return output;
    }

    private string HandleRequest(JsonNode id, string method, JsonNode? parameters)
    {
        if (method == "initialize")
        {
            if (_initialized)
            {
                return Error(id, LspErrorCodes.InvalidRequest, "Already initialized.");
            }

            _initialized = true;
            return Result(id, InitializeResult());
        }

        if (!_initialized)
        {
            return Error(id, LspErrorCodes.ServerNotInitialized, "The server is not initialized.");
        }

        if (_shutdown)
        {
            return Error(id, LspErrorCodes.InvalidRequest, "The server is shut down; only exit is accepted.");
        }

        switch (method)
        {
            case "shutdown":
                _shutdown = true;
                return Result(id, null);
            case "textDocument/completion":
                return WithPosition(id, parameters, Complete);
            case "textDocument/hover":
                return WithPosition(id, parameters, Hover);
            case "textDocument/signatureHelp":
                return WithPosition(id, parameters, SignatureHelp);
            case "textDocument/formatting":
                return WithDocument(id, parameters, Format);
            case "textDocument/documentSymbol":
                return WithDocument(id, parameters, Symbols);
            default:
                return Error(id, LspErrorCodes.MethodNotFound, $"Method not found: {method}");
        }
    }

    private void HandleNotification(string method, JsonNode? parameters, List<string> output)
    {
        if (!_initialized || _shutdown)
        {
            return;
        }

        switch (method)
        {
            case "textDocument/didOpen":
                DidOpen(parameters, output);
                break;
            case "textDocument/didChange":
                DidChange(parameters, output);
                break;
            case "textDocument/didClose":
                DidClose(parameters, output);
                break;
            default:
                // initialized, $/cancelRequest, $/setTrace, configuration changes: nothing to do.
                break;
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["positionEncoding"] = "utf-16",
            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",", "\"") },
            ["hoverProvider"] = true,
            ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") },
            ["documentFormattingProvider"] = true,
            ["documentSymbolProvider"] = true,
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "Bazaar Rubedo Language Server",
            ["version"] = typeof(LanguageServerCore).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        },
        ["experimental"] = new JsonObject { ["dslVersion"] = RubedoLanguage.Version },
    };

    private void DidOpen(JsonNode? parameters, List<string> output)
    {
        JsonNode? item = parameters?["textDocument"];
        string? uri = GetString(item?["uri"]);
        string? languageId = GetString(item?["languageId"]);
        string? text = GetString(item?["text"]);
        if (uri is null || text is null || languageId is not (RubedoLanguageId or ViewLanguageId))
        {
            return;
        }

        if (!_documents.ContainsKey(uri) && _documents.Count >= MaxOpenDocuments)
        {
            return;
        }

        var document = new Document(languageId, text, GetInt(item?["version"]));
        _documents[uri] = document;
        Publish(uri, document, output);
    }

    private void DidChange(JsonNode? parameters, List<string> output)
    {
        string? uri = GetString(parameters?["textDocument"]?["uri"]);
        if (uri is null || !_documents.TryGetValue(uri, out Document? document) || parameters?["contentChanges"] is not JsonArray changes)
        {
            return;
        }

        document.Version = GetInt(parameters["textDocument"]?["version"]) ?? document.Version;
        foreach (JsonNode? change in changes)
        {
            string? text = GetString(change?["text"]);
            if (text is null)
            {
                continue;
            }

            if (change?["range"] is { } range
                && GetInt(range["start"]?["line"]) is { } startLine && GetInt(range["start"]?["character"]) is { } startCharacter
                && GetInt(range["end"]?["line"]) is { } endLine && GetInt(range["end"]?["character"]) is { } endCharacter)
            {
                // Incremental edits are not advertised, but a client may send them; applying them is cheap and safe.
                int start = TextPositions.ToOffset(document.Text, startLine, startCharacter);
                int end = Math.Max(start, TextPositions.ToOffset(document.Text, endLine, endCharacter));
                document.Text = string.Concat(document.Text.AsSpan(0, start), text, document.Text.AsSpan(end));
            }
            else
            {
                document.Text = text;
            }
        }

        Publish(uri, document, output);
    }

    private void DidClose(JsonNode? parameters, List<string> output)
    {
        string? uri = GetString(parameters?["textDocument"]?["uri"]);
        if (uri is not null && _documents.Remove(uri))
        {
            output.Add(Notification("textDocument/publishDiagnostics", new LspPublishDiagnosticsParams(uri, null, []), LspJsonContext.Default.LspPublishDiagnosticsParams));
        }
    }

    private void Publish(string uri, Document document, List<string> output)
    {
        IReadOnlyList<AnalysisDiagnostic> found = document.IsView ? RubedoAnalyzer.DiagnoseView(document.Text) : RubedoAnalyzer.Diagnose(document.Text, _schema);
        var diagnostics = found
            .Select(d => new LspDiagnostic(TextPositions.ToRange(document.Text, d.Start, d.End), 1, d.Code, "rubedo", d.Message))
            .ToList();
        output.Add(Notification(
            "textDocument/publishDiagnostics",
            new LspPublishDiagnosticsParams(uri, document.Version, diagnostics),
            LspJsonContext.Default.LspPublishDiagnosticsParams));
    }

    private string WithDocument(JsonNode id, JsonNode? parameters, Func<Document, JsonNode?> handler)
    {
        string? uri = GetString(parameters?["textDocument"]?["uri"]);
        if (uri is null)
        {
            return Error(id, LspErrorCodes.InvalidParams, "textDocument.uri is required.");
        }

        return Result(id, _documents.TryGetValue(uri, out Document? document) ? handler(document) : null);
    }

    private string WithPosition(JsonNode id, JsonNode? parameters, Func<Document, int, JsonNode?> handler)
    {
        if (GetInt(parameters?["position"]?["line"]) is not { } line || GetInt(parameters?["position"]?["character"]) is not { } character)
        {
            return Error(id, LspErrorCodes.InvalidParams, "position is required.");
        }

        return WithDocument(id, parameters, document => handler(document, TextPositions.ToOffset(document.Text, line, character)));
    }

    private JsonNode? Complete(Document document, int offset)
    {
        CompletionList list = document.IsView ? RubedoAnalyzer.CompleteView(document.Text, offset) : RubedoAnalyzer.Complete(document.Text, offset, _schema);
        LspRange replace = TextPositions.ToRange(document.Text, list.ReplaceStart, list.ReplaceEnd);
        var items = list.Items
            .Select(i => new LspCompletionItem(
                i.Label,
                KindOf(i.Kind),
                i.Detail,
                i.Rank.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + i.Label,
                new LspTextEdit(replace, i.InsertText)))
            .ToList();
        return JsonSerializer.SerializeToNode(new LspCompletionList(false, items), LspJsonContext.Default.LspCompletionList);
    }

    private static int KindOf(CompletionKind kind) => kind switch
    {
        CompletionKind.Identifier => 6, // Variable
        CompletionKind.Function => 3,
        CompletionKind.Keyword => 14,
        CompletionKind.Widget => 7, // Class
        CompletionKind.Property => 10,
        CompletionKind.Action => 23, // Event
        CompletionKind.ActionKind => 20, // EnumMember
        CompletionKind.DataType => 22, // Struct
        _ => 5, // Field
    };

    private JsonNode? Hover(Document document, int offset)
    {
        if (document.IsView || RubedoAnalyzer.Hover(document.Text, offset, _schema) is not { } hover)
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(
            new LspHover(new LspMarkupContent("plaintext", hover.Text), TextPositions.ToRange(document.Text, hover.Start, hover.End)),
            LspJsonContext.Default.LspHover);
    }

    private static JsonNode? SignatureHelp(Document document, int offset)
    {
        if (document.IsView || RubedoAnalyzer.SignatureHelp(document.Text, offset) is not { } help)
        {
            return null;
        }

        var signature = new LspSignatureInformation(help.Label, help.Documentation, help.Parameters.Select(p => new LspParameterInformation(p)).ToList());
        return JsonSerializer.SerializeToNode(new LspSignatureHelp([signature], 0, help.ActiveParameter), LspJsonContext.Default.LspSignatureHelp);
    }

    private JsonNode? Format(Document document)
    {
        // Views have no formatter (doc 41d section 3 covers expressions only).
        if (document.IsView || Encoding.UTF8.GetByteCount(document.Text) > RubedoAnalyzer.MaxDocumentBytes)
        {
            return null;
        }

        RubedoFormatResult result = RubedoFormatter.Format(document.Text, _schema);
        if (result.Formatted is not { } formatted || formatted == document.Text)
        {
            return new JsonArray();
        }

        IReadOnlyList<LspTextEdit> edits = [new LspTextEdit(TextPositions.ToRange(document.Text, 0, document.Text.Length), formatted)];
        return JsonSerializer.SerializeToNode(edits, LspJsonContext.Default.IReadOnlyListLspTextEdit);
    }

    private static JsonNode? Symbols(Document document)
    {
        IReadOnlyList<LspDocumentSymbol> symbols = document.IsView && Encoding.UTF8.GetByteCount(document.Text) <= RubedoAnalyzer.MaxDocumentBytes
            ? ViewSymbols.Read(document.Text)
            : [];
        return JsonSerializer.SerializeToNode(symbols, LspJsonContext.Default.IReadOnlyListLspDocumentSymbol);
    }

    private static string? GetString(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static int? GetInt(JsonNode? node) => node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    // A request id is a string or a number; anything else is invalid.
    private static JsonNode? IdOf(JsonObject request) =>
        request["id"] is JsonValue value && (value.TryGetValue(out string? _) || value.TryGetValue(out long _) || value.TryGetValue(out double _))
            ? value.DeepClone()
            : null;

    private static string Result(JsonNode id, JsonNode? result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();

    private static string Notification<T>(string method, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(value, info) }.ToJsonString();
}
