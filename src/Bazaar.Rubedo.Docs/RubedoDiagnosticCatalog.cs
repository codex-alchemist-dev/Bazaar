namespace Bazaar.Rubedo.Docs;

/// <summary>One documented diagnostic code. <see cref="Area"/> is "expression" (compile and evaluation) or "view" (view documents).</summary>
public sealed record RubedoDiagnosticEntry(string Code, string Area, string Explanation);

/// <summary>
/// Words for every stable diagnostic code (doc 41a section 7, doc 41c section 8). The codes themselves are the engine's;
/// a test scans the engine source for code literals and fails when one is missing here, so this list cannot fall behind.
/// </summary>
public static class RubedoDiagnosticCatalog
{
    /// <summary>Every code, expression codes first, each group sorted by code (ordinal).</summary>
    public static IReadOnlyList<RubedoDiagnosticEntry> Entries { get; } = Build();

    private static List<RubedoDiagnosticEntry> Build()
    {
        (string Code, string Text)[] expression =
        [
            ("arity", "A function was called with the wrong number of arguments."),
            ("bad-character", "The source contains a character the language does not use."),
            ("bad-number", "A number literal is malformed."),
            ("bad-range", "A function was given arguments that are out of order, such as `clamp` with `lo` above `hi`. Raised at evaluation."),
            ("bad-string", "A string literal contains a character it may not (a string has no escape sequences)."),
            ("budget", "Evaluation ran out of its step budget. Raised at evaluation."),
            ("chained-comparison", "Relational or equality operators were chained, as in `a < b < c`. Add parentheses to combine them."),
            ("context-type", "A value supplied in the context does not have the type the schema declares for that identifier. Raised at evaluation."),
            ("division-by-zero", "A division or modulo had a zero divisor. Raised at evaluation."),
            ("internal", "The engine met a syntax node or operator it does not know. It indicates a bug in the engine, not in the source."),
            ("missing-value", "The context has no value for an identifier the expression needed. Raised at evaluation; a missing value is never defaulted."),
            ("number-out-of-range", "A number left the range of plus or minus 10^15. Raised at evaluation."),
            ("result-type", "The expression compiled, but its result type is not the one the caller requires, such as a visibility rule that is not `bool`."),
            ("string-too-long", "A string literal is longer than the string limit."),
            ("syntax", "The source does not follow the grammar."),
            ("too-deep", "Parentheses, unary operators or call arguments nest deeper than the depth limit."),
            ("too-long", "The source is longer than the source-length limit."),
            ("too-many-tokens", "The source has more tokens than the token limit."),
            ("type", "An operator or function was given operands or arguments of the wrong type. There are no implicit conversions."),
            ("unknown-function", "A call names a function that is not in the closed function table."),
            ("unknown-identifier", "The source names an identifier that is not in the schema."),
            ("unterminated-string", "A string literal is missing its closing quote."),
        ];

        (string Code, string Text)[] view =
        [
            ("view.bad_data_name", "A data source name is not a valid identifier."),
            ("view.bad_field_name", "A field of a list source is not a valid identifier."),
            ("view.bad_name", "The view name is not dotted lower-case segments, such as `bazaar.gc.feed`."),
            ("view.data_shadows_context", "A data source has the name of a context identifier."),
            ("view.dsl_version", "The document's `dslVersion` is not one this reader supports."),
            ("view.item_field_shadows", "A field of a list source has the name of something already in scope."),
            ("view.json", "The document is not valid JSON."),
            ("view.list_key", "A list's `key` does not name a field of its source."),
            ("view.list_source", "A list's `over` does not name a declared list source."),
            ("view.list_template", "A list does not have exactly one item template."),
            ("view.missing_label", "A `button`, `icon` or `input` has no label."),
            ("view.missing_icon_name", "An `icon` has no `name` from the icon set."),
            ("view.unknown_token", "A token reference names a token that is not in the catalog."),
            ("view.token_kind_mismatch", "A token's kind does not fit the prop it is used in."),
            ("view.literal_style_value", "A style prop holds a raw colour, duration or motion name instead of a token."),
            ("view.shape", "The document's JSON has the wrong shape: a missing or mistyped field."),
            ("view.tab_child", "A child of `tabs` is not a tab."),
            ("view.tab_page", "A tab does not have exactly one page child."),
            ("view.tab_placement", "A tab appears anywhere other than directly under `tabs`."),
            ("view.tab_title", "A tab has no title."),
            ("view.tabs_empty", "A `tabs` node has no tabs."),
            ("view.unexpected_children", "A leaf widget has children."),
            ("view.unknown_action", "A button refers to an action that is not declared."),
            ("view.unknown_action_kind", "An action has a kind outside the closed set."),
            ("view.unknown_widget", "A node names a widget outside the closed set."),
        ];

        var list = new List<RubedoDiagnosticEntry>();
        list.AddRange(expression.OrderBy(e => e.Code, StringComparer.Ordinal).Select(e => new RubedoDiagnosticEntry(e.Code, "expression", e.Text)));
        list.AddRange(view.OrderBy(e => e.Code, StringComparer.Ordinal).Select(e => new RubedoDiagnosticEntry(e.Code, "view", e.Text)));
        return list;
    }
}
