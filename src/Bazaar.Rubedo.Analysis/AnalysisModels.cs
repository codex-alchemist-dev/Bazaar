using Bazaar.Rubedo;

namespace Bazaar.Rubedo.Analysis;

/// <summary>
/// A problem with a range (doc 41d section 4). <see cref="Start"/> and <see cref="End"/> are character offsets into the analysed
/// text, <c>0 &lt;= Start &lt;= End &lt;= length</c>; the caller maps them to line and column. <see cref="Code"/> is the stable
/// engine code (doc 41a section 7) or a code prefixed <c>analysis.</c> or <c>view.</c>.
/// </summary>
public sealed record AnalysisDiagnostic(string Code, string Message, int Start, int End);

/// <summary>What a completion item stands for.</summary>
public enum CompletionKind
{
    Identifier,
    Function,
    Keyword,
    Widget,
    Property,
    Action,
    ActionKind,
    DataType,
    DocumentKey,
    Token,
}

/// <summary>
/// One completion. <see cref="Type"/> is the value type for expression items, null otherwise. <see cref="Rank"/> is 0 for the
/// best matches (for example the type an argument position expects) and larger for the rest; lists are already sorted.
/// </summary>
public sealed record CompletionItem(string Label, CompletionKind Kind, string Detail, string InsertText, RubedoType? Type, int Rank);

/// <summary>Completions and the range of text they replace (empty when the cursor sits between tokens).</summary>
public sealed record CompletionList(int ReplaceStart, int ReplaceEnd, IReadOnlyList<CompletionItem> Items);

/// <summary>Hover text (plain text, no markup) for the token or group in <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed record HoverInfo(string Text, int Start, int End);

/// <summary>
/// Signature help inside a call. <see cref="ActiveParameter"/> is the zero-based argument index the cursor is in; it is equal to
/// the parameter count when the call already has more arguments than the signature takes.
/// </summary>
public sealed record SignatureHelpInfo(string Label, IReadOnlyList<string> Parameters, int ActiveParameter, string Documentation);
