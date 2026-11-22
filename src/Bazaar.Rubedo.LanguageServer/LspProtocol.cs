using System.Text.Json.Serialization;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>LSP position: zero-based line and UTF-16 code unit column.</summary>
public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record LspDiagnostic(LspRange Range, int Severity, string Code, string Source, string Message);

public sealed record LspTextEdit(LspRange Range, string NewText);

public sealed record LspCompletionItem(string Label, int Kind, string Detail, string SortText, LspTextEdit TextEdit);

public sealed record LspCompletionList(bool IsIncomplete, IReadOnlyList<LspCompletionItem> Items);

public sealed record LspMarkupContent(string Kind, string Value);

public sealed record LspHover(LspMarkupContent Contents, LspRange Range);

public sealed record LspParameterInformation(string Label);

public sealed record LspSignatureInformation(string Label, string Documentation, IReadOnlyList<LspParameterInformation> Parameters);

public sealed record LspSignatureHelp(IReadOnlyList<LspSignatureInformation> Signatures, int ActiveSignature, int ActiveParameter);

public sealed record LspDocumentSymbol(string Name, int Kind, LspRange Range, LspRange SelectionRange, IReadOnlyList<LspDocumentSymbol> Children);

public sealed record LspPublishDiagnosticsParams(string Uri, int? Version, IReadOnlyList<LspDiagnostic> Diagnostics);

/// <summary>Source-generated serializer metadata for every payload the server builds itself. Incoming messages are read as JSON nodes.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LspCompletionList))]
[JsonSerializable(typeof(LspHover))]
[JsonSerializable(typeof(LspSignatureHelp))]
[JsonSerializable(typeof(IReadOnlyList<LspTextEdit>))]
[JsonSerializable(typeof(IReadOnlyList<LspDocumentSymbol>))]
[JsonSerializable(typeof(LspPublishDiagnosticsParams))]
public sealed partial class LspJsonContext : JsonSerializerContext
{
}

/// <summary>JSON-RPC error codes used by the server.</summary>
public static class LspErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int ServerNotInitialized = -32002;
}
