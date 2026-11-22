using System.Text;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Analysis;

/// <summary>
/// Transport-neutral analysis of Rubedo sources and view documents (doc 41d section 4). Every entry point is pure and
/// deterministic (no clock, no I/O, no randomness), bounded by the language limits plus a 64 KiB document cap, and never throws
/// on bad input: problems come back as diagnostics or as empty results. Offsets are character offsets, and cursor offsets
/// outside the text are clamped.
/// </summary>
public static partial class RubedoAnalyzer
{
    /// <summary>The largest document any entry point looks at, in UTF-8 bytes (doc 41d section 4).</summary>
    public const int MaxDocumentBytes = 64 * 1024;

    private static readonly Dictionary<string, RubedoFunctionSignature> _functions =
        RubedoFunctionTable.Signatures.ToDictionary(s => s.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> _operatorHelp = new(StringComparer.Ordinal)
    {
        ["||"] = "Logical or: bool, bool gives bool.",
        ["&&"] = "Logical and: bool, bool gives bool.",
        ["=="] = "Equal: two values of the same type give bool.",
        ["!="] = "Not equal: two values of the same type give bool.",
        ["<"] = "Less than: number, number gives bool.",
        ["<="] = "Less than or equal: number, number gives bool.",
        [">"] = "Greater than: number, number gives bool.",
        [">="] = "Greater than or equal: number, number gives bool.",
        ["+"] = "Add: number, number gives number.",
        ["-"] = "Subtract (or negate): number gives number.",
        ["*"] = "Multiply: number, number gives number.",
        ["/"] = "Divide: number, number gives number. An error when dividing by zero.",
        ["%"] = "Remainder: number, number gives number. An error when dividing by zero.",
        ["!"] = "Logical not: bool gives bool.",
    };

    /// <summary>
    /// Compiles <paramref name="source"/> and reports the engine's diagnostics with start and end offsets. The end is the end of
    /// the token the engine pointed at, or the start itself when the problem is not on a token (for example the end of input).
    /// An empty list means the source is valid.
    /// </summary>
    public static IReadOnlyList<AnalysisDiagnostic> Diagnose(string? source, RubedoSchema? schema = null, RubedoType? requiredType = null)
    {
        source ??= string.Empty;
        if (IsOverCap(source))
        {
            return [TooLarge(source.Length)];
        }

        RubedoCompileResult result = RubedoEngine.Compile(source, schema, requiredType);
        if (result.Diagnostics.Count == 0)
        {
            return [];
        }

        List<ScanToken> tokens = ExpressionScanner.Scan(source);
        return result.Diagnostics.Select(d => ToRange(d, source, tokens)).ToArray();
    }

    /// <summary>
    /// Completions at <paramref name="offset"/>: context identifiers with their type, functions with their signature, and
    /// <c>true</c> and <c>false</c>. Inside a call, items of the type the argument position expects rank first. Nothing is offered
    /// inside a string, inside a number, or where an operator rather than an operand is expected.
    /// </summary>
    public static CompletionList Complete(string? source, int offset, RubedoSchema? schema = null)
    {
        source ??= string.Empty;
        schema ??= RubedoSchema.Standard;
        offset = Math.Clamp(offset, 0, source.Length);
        if (IsOverCap(source))
        {
            return new CompletionList(offset, offset, []);
        }

        List<ScanToken> tokens = ExpressionScanner.Scan(source);
        if (tokens.Any(t => (t.Kind == ScanKind.String && t.Start < offset && (t.IsTerminatedString ? offset < t.End : offset <= t.End))
                            || (t.Kind == ScanKind.Number && t.Start < offset && offset <= t.End)))
        {
            return new CompletionList(offset, offset, []);
        }

        ScanToken? word = null;
        foreach (ScanToken t in tokens)
        {
            if (t.Kind is ScanKind.Identifier or ScanKind.Keyword && t.Start < offset && offset <= t.End)
            {
                word = t;
            }
        }

        int replaceStart = word?.Start ?? offset;
        int replaceEnd = word?.End ?? offset;
        string prefix = word is { } w ? source[w.Start..offset] : string.Empty;

        ScanToken? previous = null;
        foreach (ScanToken t in tokens)
        {
            if (t.End <= replaceStart)
            {
                previous = t;
            }
        }

        if (previous is { IsOperandEnd: true })
        {
            return new CompletionList(replaceStart, replaceEnd, []);
        }

        RubedoType? expected = ExpectedType(source, tokens, offset, schema);
        var items = new List<CompletionItem>();
        foreach (KeyValuePair<string, RubedoType> identifier in schema.Identifiers)
        {
            items.Add(new CompletionItem(identifier.Key, CompletionKind.Identifier, FunctionDocs.TypeName(identifier.Value), identifier.Key, identifier.Value, 0));
        }

        foreach (RubedoFunctionSignature function in RubedoFunctionTable.Signatures)
        {
            items.Add(new CompletionItem(function.Name, CompletionKind.Function, FunctionDocs.Label(function), function.Name + "(", function.Result, 0));
        }

        foreach (string keyword in (string[])["false", "true"])
        {
            items.Add(new CompletionItem(keyword, CompletionKind.Keyword, "bool", keyword, RubedoType.Bool, 0));
        }

        IEnumerable<CompletionItem> ranked = items
            .Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(i => expected is { } e && i.Type != e ? i with { Rank = 1 } : i)
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Label, StringComparer.Ordinal);
        return new CompletionList(replaceStart, replaceEnd, ranked.ToArray());
    }

    /// <summary>
    /// Hover text for the token at <paramref name="offset"/> (or the one ending there): the type of an identifier, the
    /// signature of a function, the type of a literal, what an operator does, or the inferred type of a parenthesised group.
    /// Null when there is nothing to say.
    /// </summary>
    public static HoverInfo? Hover(string? source, int offset, RubedoSchema? schema = null)
    {
        source ??= string.Empty;
        schema ??= RubedoSchema.Standard;
        offset = Math.Clamp(offset, 0, source.Length);
        if (IsOverCap(source))
        {
            return null;
        }

        List<ScanToken> tokens = ExpressionScanner.Scan(source);
        int index = tokens.FindIndex(t => t.Start <= offset && offset < t.End);
        if (index < 0)
        {
            index = tokens.FindIndex(t => t.End == offset);
        }

        if (index < 0)
        {
            return null;
        }

        ScanToken token = tokens[index];
        string? text = null;
        switch (token.Kind)
        {
            case ScanKind.Identifier:
                bool isCall = index + 1 < tokens.Count && tokens[index + 1].Kind == ScanKind.LeftParen;
                if (isCall)
                {
                    text = _functions.TryGetValue(token.Text, out RubedoFunctionSignature? function)
                        ? $"{FunctionDocs.Label(function)}\n{FunctionDocs.Description(function.Name)}"
                        : $"'{token.Text}' is not a Rubedo function.";
                }
                else
                {
                    text = schema.TryGetType(token.Text, out RubedoType type)
                        ? $"{token.Text}: {FunctionDocs.TypeName(type)} (context identifier)"
                        : $"'{token.Text}' is not in the context.";
                }

                break;
            case ScanKind.Keyword:
                text = $"{token.Text}: bool literal";
                break;
            case ScanKind.Number:
                text = "number literal";
                break;
            case ScanKind.String:
                text = "text literal";
                break;
            case ScanKind.Operator:
                text = _operatorHelp.GetValueOrDefault(token.Text);
                break;
            case ScanKind.LeftParen:
            case ScanKind.RightParen:
                text = GroupType(source, tokens, index, schema);
                break;
        }

        return text is null ? null : new HoverInfo(text, token.Start, token.End);
    }

    /// <summary>
    /// Signature help when <paramref name="offset"/> is inside the argument list of a known function. Null outside a call and for
    /// a name that is not a function.
    /// </summary>
    public static SignatureHelpInfo? SignatureHelp(string? source, int offset)
    {
        source ??= string.Empty;
        offset = Math.Clamp(offset, 0, source.Length);
        if (IsOverCap(source))
        {
            return null;
        }

        List<ScanToken> tokens = ExpressionScanner.Scan(source);
        if (EnclosingCall(tokens, offset) is not { Signature: { } signature } call)
        {
            return null;
        }

        int active = signature.IsVariadic ? Math.Min(call.ArgIndex, signature.Parameters.Count - 1) : Math.Min(call.ArgIndex, signature.Parameters.Count);
        string[] parameters = Enumerable.Range(0, signature.Parameters.Count).Select(i => FunctionDocs.ParameterLabel(signature, i)).ToArray();
        return new SignatureHelpInfo(FunctionDocs.Label(signature), parameters, active, FunctionDocs.Description(signature.Name));
    }

    private static bool IsOverCap(string text) =>
        text.Length > MaxDocumentBytes || (text.Length * 3 > MaxDocumentBytes && Encoding.UTF8.GetByteCount(text) > MaxDocumentBytes);

    private static AnalysisDiagnostic TooLarge(int length) =>
        new("analysis.too_large", $"A document may have at most {MaxDocumentBytes} bytes.", 0, length);

    private static AnalysisDiagnostic ToRange(RubedoDiagnostic diagnostic, string source, List<ScanToken> tokens)
    {
        if (diagnostic.Offset < 0)
        {
            return new AnalysisDiagnostic(diagnostic.Code, diagnostic.Message, 0, source.Length);
        }

        int start = Math.Min(diagnostic.Offset, source.Length);
        if (diagnostic.Code == "too-long")
        {
            return new AnalysisDiagnostic(diagnostic.Code, diagnostic.Message, start, source.Length);
        }

        int end = start;
        foreach (ScanToken t in tokens)
        {
            if (t.Start <= start && start < t.End)
            {
                end = t.End;
                break;
            }
        }

        return new AnalysisDiagnostic(diagnostic.Code, diagnostic.Message, start, end);
    }

    private sealed record CallContext(string Name, RubedoFunctionSignature? Signature, int ArgIndex, int ArgsStart, int FirstArgEnd);

    private sealed class Frame(string? callName, int argsStart)
    {
        public string? CallName { get; } = callName;

        public int ArgsStart { get; } = argsStart;

        public int ArgIndex { get; set; }

        public int FirstArgEnd { get; set; } = -1;
    }

    /// <summary>The innermost call whose argument list contains the cursor, found by replaying the tokens before it.</summary>
    private static CallContext? EnclosingCall(List<ScanToken> tokens, int offset)
    {
        var stack = new List<Frame>();
        for (int i = 0; i < tokens.Count; i++)
        {
            ScanToken t = tokens[i];
            if (t.End > offset)
            {
                break;
            }

            switch (t.Kind)
            {
                case ScanKind.LeftParen:
                    bool isCall = i > 0 && tokens[i - 1].Kind == ScanKind.Identifier;
                    stack.Add(new Frame(isCall ? tokens[i - 1].Text : null, t.End));
                    break;
                case ScanKind.RightParen:
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
                case ScanKind.Comma:
                    if (stack.Count > 0)
                    {
                        Frame top = stack[^1];
                        if (top.ArgIndex == 0)
                        {
                            top.FirstArgEnd = t.Start;
                        }

                        top.ArgIndex++;
                    }

                    break;
            }
        }

        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].CallName is { } name)
            {
                Frame frame = stack[i];
                return new CallContext(name, _functions.GetValueOrDefault(name), frame.ArgIndex, frame.ArgsStart, frame.FirstArgEnd);
            }
        }

        return null;
    }

    /// <summary>The type the argument position at the cursor expects, or null when unknown or not inside a call.</summary>
    private static RubedoType? ExpectedType(string source, List<ScanToken> tokens, int offset, RubedoSchema schema)
    {
        if (EnclosingCall(tokens, offset) is not { Signature: { } signature } call)
        {
            return null;
        }

        int index = Math.Min(call.ArgIndex, signature.Parameters.Count - 1);
        if (call.ArgIndex >= signature.Parameters.Count && !signature.IsVariadic)
        {
            return null;
        }

        if (signature.Parameters[index] is { } fixedType)
        {
            return fixedType;
        }

        // inSet: the first argument decides the candidate type.
        if (call.ArgIndex == 0 || call.FirstArgEnd < call.ArgsStart)
        {
            return null;
        }

        return InferType(source[call.ArgsStart..call.FirstArgEnd], schema);
    }

    private static RubedoType? InferType(string text, RubedoSchema schema)
    {
        RubedoCompileResult result = RubedoEngine.Compile(text, schema);
        return result.Program?.ResultType;
    }

    /// <summary>The inferred type of the group a parenthesis belongs to, when the text inside it is an expression on its own.</summary>
    private static string? GroupType(string source, List<ScanToken> tokens, int index, RubedoSchema schema)
    {
        int open;
        int close;
        if (tokens[index].Kind == ScanKind.LeftParen)
        {
            open = index;
            close = FindMatch(tokens, index, 1);
        }
        else
        {
            close = index;
            open = FindMatch(tokens, index, -1);
        }

        if (open < 0 || close < 0)
        {
            return null;
        }

        RubedoType? type = InferType(source[tokens[open].End..tokens[close].Start], schema);
        return type is { } t ? $"Expression of type {FunctionDocs.TypeName(t)}" : null;
    }

    private static int FindMatch(List<ScanToken> tokens, int from, int step)
    {
        int depth = 0;
        for (int i = from; i >= 0 && i < tokens.Count; i += step)
        {
            if (tokens[i].Kind == ScanKind.LeftParen)
            {
                depth += step;
            }
            else if (tokens[i].Kind == ScanKind.RightParen)
            {
                depth -= step;
            }

            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
