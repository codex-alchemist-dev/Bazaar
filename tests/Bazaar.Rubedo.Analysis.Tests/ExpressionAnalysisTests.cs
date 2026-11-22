using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Analysis.Tests;

public class ExpressionAnalysisTests
{
    // ---- Diagnose ----

    [Fact]
    public void Valid_source_has_no_diagnostics()
    {
        Assert.Empty(RubedoAnalyzer.Diagnose("widthDp >= 600 && !reduceMotion"));
    }

    [Theory]
    [InlineData("widthDp >= ", "syntax", 11, 11)]
    [InlineData("widthDp >= 600 +", "syntax", 16, 16)]
    [InlineData("foo > 1", "unknown-identifier", 0, 3)]
    [InlineData("widthDp > 1 && bogus", "unknown-identifier", 15, 20)]
    [InlineData("nope(1)", "unknown-function", 0, 4)]
    [InlineData("min(1)", "arity", 0, 3)]
    [InlineData("\"abc", "unterminated-string", 0, 4)]
    [InlineData("1 + @", "bad-character", 4, 5)]
    [InlineData("1.", "bad-number", 0, 2)]
    [InlineData("1 < 2 < 3", "chained-comparison", 6, 7)]
    [InlineData("1 + \"a\"", "type", 2, 3)]
    [InlineData("contains(1, \"a\")", "type", 9, 10)]
    public void Diagnostics_carry_start_and_end_of_the_token(string source, string code, int start, int end)
    {
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose(source));
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(start, diagnostic.Start);
        Assert.Equal(end, diagnostic.End);
    }

    [Fact]
    public void Diagnostic_codes_match_the_engine()
    {
        foreach (string source in new[] { "x", "1 +", "min(", "\"a\\b\"", "1 == \"a\"" })
        {
            RubedoDiagnostic engine = Assert.Single(RubedoEngine.Compile(source).Diagnostics);
            AnalysisDiagnostic analysis = Assert.Single(RubedoAnalyzer.Diagnose(source));
            Assert.Equal(engine.Code, analysis.Code);
            Assert.Equal(engine.Message, analysis.Message);
            Assert.Equal(engine.Offset, analysis.Start);
        }
    }

    [Fact]
    public void Required_type_is_reported()
    {
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose("1 + 2", requiredType: RubedoType.Bool));
        Assert.Equal("result-type", diagnostic.Code);
    }

    [Fact]
    public void Source_over_the_language_limit_reports_too_long_over_its_tail()
    {
        string source = new string('1', 100) + " + " + new string('2', 450);
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose(source));
        Assert.Equal("too-long", diagnostic.Code);
        Assert.Equal(512, diagnostic.Start);
        Assert.Equal(source.Length, diagnostic.End);
    }

    [Fact]
    public void Too_many_tokens_is_reported_on_a_token()
    {
        string source = string.Join("+", Enumerable.Repeat("1", 200));
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose(source.Length > 512 ? source[..500] : source));
        Assert.True(diagnostic.Start <= diagnostic.End);
    }

    [Fact]
    public void Document_over_the_cap_returns_a_diagnostic_and_every_entry_point_stays_quiet()
    {
        string huge = new string('a', RubedoAnalyzer.MaxDocumentBytes + 1);
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose(huge));
        Assert.Equal("analysis.too_large", diagnostic.Code);
        Assert.Empty(RubedoAnalyzer.Complete(huge, 3).Items);
        Assert.Null(RubedoAnalyzer.Hover(huge, 3));
        Assert.Null(RubedoAnalyzer.SignatureHelp(huge, 3));
        Assert.Equal("analysis.too_large", Assert.Single(RubedoAnalyzer.DiagnoseView(huge)).Code);
        Assert.Empty(RubedoAnalyzer.CompleteView(huge, 3).Items);
    }

    [Fact]
    public void Cap_counts_utf8_bytes_not_characters()
    {
        string wide = new string('€', 30_000); // 3 bytes each, 90,000 bytes
        Assert.Equal("analysis.too_large", Assert.Single(RubedoAnalyzer.Diagnose(wide)).Code);
    }

    [Fact]
    public void Surrogate_pair_bad_character_is_one_range()
    {
        AnalysisDiagnostic diagnostic = Assert.Single(RubedoAnalyzer.Diagnose("1 + \U0001F600"));
        Assert.Equal("bad-character", diagnostic.Code);
        Assert.Equal(4, diagnostic.Start);
        Assert.Equal(6, diagnostic.End);
    }

    [Fact]
    public void Null_and_empty_input_do_not_throw()
    {
        Assert.Equal("syntax", Assert.Single(RubedoAnalyzer.Diagnose(null)).Code);
        Assert.Equal("syntax", Assert.Single(RubedoAnalyzer.Diagnose(string.Empty)).Code);
        Assert.NotEmpty(RubedoAnalyzer.Complete(null, 0).Items);
        Assert.Null(RubedoAnalyzer.Hover(null, 0));
        Assert.Null(RubedoAnalyzer.SignatureHelp(null, 0));
    }

    [Fact]
    public void Custom_schema_is_used()
    {
        var schema = new RubedoSchema(new Dictionary<string, RubedoType> { ["unread"] = RubedoType.Number });
        Assert.Empty(RubedoAnalyzer.Diagnose("unread > 0", schema));
        Assert.Equal("unknown-identifier", Assert.Single(RubedoAnalyzer.Diagnose("widthDp > 0", schema)).Code);
    }

    // ---- Complete ----

    [Fact]
    public void Empty_source_offers_identifiers_functions_and_keywords()
    {
        CompletionList list = RubedoAnalyzer.Complete(string.Empty, 0);
        Assert.Equal(RubedoSchema.Standard.Identifiers.Count, list.Items.Count(i => i.Kind == CompletionKind.Identifier));
        Assert.Equal(RubedoFunctionTable.Signatures.Count, list.Items.Count(i => i.Kind == CompletionKind.Function));
        Assert.Contains(list.Items, i => i is { Label: "true", Kind: CompletionKind.Keyword });
        Assert.Contains(list.Items, i => i is { Label: "false", Kind: CompletionKind.Keyword });
        Assert.Contains(list.Items, i => i is { Label: "widthDp", Type: RubedoType.Number, Detail: "number" });
    }

    [Fact]
    public void Prefix_filters_and_sets_the_replace_range()
    {
        CompletionList list = RubedoAnalyzer.Complete("width", 5);
        Assert.Equal(0, list.ReplaceStart);
        Assert.Equal(5, list.ReplaceEnd);
        Assert.Equal(["widthDp"], list.Items.Select(i => i.Label));

        CompletionList mid = RubedoAnalyzer.Complete("deviceClass == \"x\" && mi", 24);
        Assert.Equal("min", Assert.Single(mid.Items).Label);
        Assert.Equal("min(", mid.Items[0].InsertText);
        Assert.Equal(22, mid.ReplaceStart);
    }

    [Fact]
    public void Function_items_show_their_signature()
    {
        CompletionItem clamp = Assert.Single(RubedoAnalyzer.Complete("cl", 2).Items);
        Assert.Equal("clamp(value: number, lo: number, hi: number): number", clamp.Detail);
        Assert.Equal(RubedoType.Number, clamp.Type);
    }

    [Fact]
    public void Inside_a_call_the_expected_type_ranks_first()
    {
        // Second argument of contains is text: text identifiers rank 0, the rest rank 1.
        CompletionList list = RubedoAnalyzer.Complete("contains(display, )", 18);
        CompletionItem first = list.Items[0];
        Assert.Equal(RubedoType.Text, first.Type);
        Assert.Equal(0, first.Rank);
        CompletionItem widthDp = Assert.Single(list.Items, i => i.Label == "widthDp");
        Assert.Equal(1, widthDp.Rank);
        Assert.True(list.Items.TakeWhile(i => i.Rank == 0).All(i => i.Type == RubedoType.Text));
        Assert.True(list.Items.SkipWhile(i => i.Rank == 0).All(i => i.Rank == 1));
    }

    [Fact]
    public void In_set_candidates_follow_the_type_of_the_first_argument()
    {
        CompletionList list = RubedoAnalyzer.Complete("inSet(widthDp, )", 15);
        Assert.Equal(RubedoType.Number, list.Items[0].Type);
        Assert.All(list.Items.TakeWhile(i => i.Rank == 0), i => Assert.Equal(RubedoType.Number, i.Type));
        Assert.Contains(list.Items, i => i is { Label: "display", Rank: 1 });
    }

    [Fact]
    public void Nested_call_uses_the_innermost_call()
    {
        CompletionList list = RubedoAnalyzer.Complete("min(abs(), 1)", 8);
        Assert.Equal(RubedoType.Number, list.Items[0].Type);
        Assert.Equal(0, list.Items[0].Rank);
    }

    [Fact]
    public void Nothing_is_offered_where_an_operator_is_expected()
    {
        Assert.Empty(RubedoAnalyzer.Complete("widthDp ", 8).Items);
        Assert.Empty(RubedoAnalyzer.Complete("(1) ", 4).Items);
        Assert.Empty(RubedoAnalyzer.Complete("widthDp wid", 11).Items);
        Assert.NotEmpty(RubedoAnalyzer.Complete("widthDp >= ", 11).Items);
        Assert.NotEmpty(RubedoAnalyzer.Complete("!", 1).Items);
        Assert.NotEmpty(RubedoAnalyzer.Complete("min(1, ", 7).Items);
    }

    [Fact]
    public void Nothing_is_offered_inside_a_string_or_a_number()
    {
        Assert.Empty(RubedoAnalyzer.Complete("display == \"in\"", 13).Items);
        Assert.Empty(RubedoAnalyzer.Complete("display == \"in", 14).Items);
        Assert.Empty(RubedoAnalyzer.Complete("widthDp > 12", 12).Items);
        Assert.NotEmpty(RubedoAnalyzer.Complete("display == \"in\" && ", 19).Items);
    }

    [Fact]
    public void Completion_is_sorted_deterministically_and_clamps_the_offset()
    {
        CompletionList a = RubedoAnalyzer.Complete("", 0);
        CompletionList b = RubedoAnalyzer.Complete("", 0);
        Assert.Equal(a.Items.Select(i => i.Label), b.Items.Select(i => i.Label));
        Assert.Equal(a.Items.OrderBy(i => i.Rank).ThenBy(i => i.Kind).ThenBy(i => i.Label, StringComparer.Ordinal).Select(i => i.Label), a.Items.Select(i => i.Label));
        CompletionList far = RubedoAnalyzer.Complete("wi", 999);
        Assert.Equal(2, far.ReplaceEnd);
        CompletionList negative = RubedoAnalyzer.Complete("wi", -5);
        Assert.Equal(0, negative.ReplaceStart);
    }

    // ---- Hover ----

    [Fact]
    public void Hover_on_an_identifier_shows_its_type()
    {
        HoverInfo hover = RubedoAnalyzer.Hover("widthDp > 1", 3)!;
        Assert.Equal("widthDp: number (context identifier)", hover.Text);
        Assert.Equal(0, hover.Start);
        Assert.Equal(7, hover.End);
    }

    [Fact]
    public void Hover_at_the_end_of_a_token_still_finds_it()
    {
        Assert.Contains("widthDp", RubedoAnalyzer.Hover("widthDp", 7)!.Text);
    }

    [Fact]
    public void Hover_on_a_function_shows_signature_and_description()
    {
        HoverInfo hover = RubedoAnalyzer.Hover("clamp(1, 2, 3) > 0", 2)!;
        Assert.StartsWith("clamp(value: number, lo: number, hi: number): number", hover.Text);
        Assert.Contains("Limits a number", hover.Text);
    }

    [Fact]
    public void Hover_covers_literals_operators_and_unknown_names()
    {
        Assert.Equal("true: bool literal", RubedoAnalyzer.Hover("true", 1)!.Text);
        Assert.Equal("number literal", RubedoAnalyzer.Hover("12 + 1", 0)!.Text);
        Assert.Equal("text literal", RubedoAnalyzer.Hover("\"a\" == \"b\"", 1)!.Text);
        Assert.Contains("number, number gives number", RubedoAnalyzer.Hover("12 + 1", 3)!.Text);
        Assert.Contains("not in the context", RubedoAnalyzer.Hover("zzz", 1)!.Text);
        Assert.Contains("not a Rubedo function", RubedoAnalyzer.Hover("zzz(1)", 1)!.Text);
        Assert.Null(RubedoAnalyzer.Hover("1   2", 2));
        Assert.Null(RubedoAnalyzer.Hover("", 0));
    }

    [Fact]
    public void Hover_on_a_parenthesis_infers_the_group_type()
    {
        Assert.Equal("Expression of type number", RubedoAnalyzer.Hover("(1 + 2) * 3 > 0", 0)!.Text);
        Assert.Equal("Expression of type bool", RubedoAnalyzer.Hover("(widthDp > 2) && true", 12)!.Text);
        Assert.Null(RubedoAnalyzer.Hover("(1 +", 0));
        Assert.Null(RubedoAnalyzer.Hover(")", 0));
    }

    [Fact]
    public void Hover_works_on_invalid_source()
    {
        Assert.Contains("number", RubedoAnalyzer.Hover("widthDp >", 2)!.Text);
    }

    // ---- Signature help ----

    [Theory]
    [InlineData("min(", 4, 0)]
    [InlineData("min(1", 5, 0)]
    [InlineData("min(1, ", 7, 1)]
    [InlineData("min(1, 2", 8, 1)]
    [InlineData("min(1, 2, 3", 11, 2)]
    [InlineData("clamp(1, 2, 3)", 13, 2)]
    public void Signature_help_tracks_the_active_parameter(string source, int offset, int active)
    {
        SignatureHelpInfo help = RubedoAnalyzer.SignatureHelp(source, offset)!;
        Assert.Equal(active, help.ActiveParameter);
    }

    [Fact]
    public void Signature_help_lists_the_parameters()
    {
        SignatureHelpInfo help = RubedoAnalyzer.SignatureHelp("startsWith(display, ", 20)!;
        Assert.Equal("startsWith(text: text, prefix: text): bool", help.Label);
        Assert.Equal(["text: text", "prefix: text"], help.Parameters);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Contains("starts with", help.Documentation);
    }

    [Fact]
    public void Signature_help_uses_the_innermost_call_and_ends_after_the_close()
    {
        Assert.Equal("abs", RubedoAnalyzer.SignatureHelp("min(abs(1), 2)", 8)!.Label[..3]);
        Assert.Equal("min", RubedoAnalyzer.SignatureHelp("min(abs(1), 2)", 12)!.Label[..3]);
        Assert.Null(RubedoAnalyzer.SignatureHelp("min(abs(1), 2)", 14));
        Assert.Null(RubedoAnalyzer.SignatureHelp("(1 + 2)", 3));
        Assert.Null(RubedoAnalyzer.SignatureHelp("nope(1, ", 8));
    }

    [Fact]
    public void Signature_help_for_in_set_is_variadic()
    {
        SignatureHelpInfo help = RubedoAnalyzer.SignatureHelp("inSet(display, \"a\", \"b\", ", 24)!;
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal("inSet(value: number|text, candidate...: number|text): bool", help.Label);
    }

    [Fact]
    public void Extra_arguments_point_past_the_last_parameter()
    {
        Assert.Equal(1, RubedoAnalyzer.SignatureHelp("abs(1, 2", 8)!.ActiveParameter);
        Assert.Equal(1, RubedoAnalyzer.SignatureHelp("abs(1, ", 7)!.ActiveParameter);
    }

    [Fact]
    public void Function_table_matches_the_checker()
    {
        foreach (RubedoFunctionSignature function in RubedoFunctionTable.Signatures)
        {
            if (function.IsVariadic)
            {
                continue;
            }

            string[] args = function.Parameters.Select(p => p switch
            {
                RubedoType.Bool => "true",
                RubedoType.Number => "1",
                _ => "\"a\"",
            }).ToArray();
            RubedoCompileResult result = RubedoEngine.Compile($"{function.Name}({string.Join(", ", args)})");
            Assert.True(result.IsValid, function.Name);
            Assert.Equal(function.Result, result.Program!.ResultType);
        }

        Assert.Equal(RubedoFunctionTable.Signatures.Select(s => s.Name).Order(StringComparer.Ordinal), RubedoFunctionTable.Signatures.Select(s => s.Name));
        Assert.Contains(RubedoFunctionTable.Signatures, s => s.Name == "inSet" && s.IsVariadic);
    }
}
