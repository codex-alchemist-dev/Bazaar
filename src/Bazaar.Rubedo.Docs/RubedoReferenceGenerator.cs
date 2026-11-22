using System.Globalization;
using System.Text;
using System.Text.Json;
using Bazaar.Rubedo.Analysis;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Docs;

/// <summary>
/// Generates the Rubedo reference (doc 41d section 6) as Markdown pages. Every fact comes from the engine: the standard schema,
/// the function table, the closed widget and action sets and the limits. The words come from <see cref="FunctionDocs"/>,
/// <see cref="RubedoDiagnosticCatalog"/> and the tables below. The output is deterministic: no clock, no environment, LF newlines,
/// and a fixed order. A closed set that gains a member this generator has no words for makes <see cref="Generate()"/> throw,
/// so the drift check fails instead of the reference silently going stale.
/// </summary>
public static class RubedoReferenceGenerator
{
    private const string _corpusResource = "rubedo-v1.json";

    private static readonly Dictionary<string, string> _identifierMeanings = new(StringComparer.Ordinal)
    {
        ["deviceClass"] = "One of `phone`, `foldable`, `tablet`, `desktop`.",
        ["display"] = "One of `single`, `cover`, `inner`.",
        ["posture"] = "One of `folded`, `halfOpen`, `unfolded`.",
        ["widthDp"] = "Window width in dp. The half-open threshold is 600.",
        ["heightDp"] = "Window height in dp.",
        ["textScale"] = "Accessibility text scale, 1.0 by default.",
        ["reduceMotion"] = "The platform's reduced-motion setting.",
        ["isOwner"] = "True when the viewer is the owner.",
        ["colorScheme"] = "One of `light`, `dark`.",
    };

    private static readonly Dictionary<ViewWidget, (string Children, string Role)> _widgets = new()
    {
        [ViewWidget.Stack] = ("many", "Vertical stack."),
        [ViewWidget.Row] = ("many", "Horizontal row, wraps by flag."),
        [ViewWidget.Grid] = ("many", "Two-dimensional grid, column count prop."),
        [ViewWidget.Scroll] = ("one", "Scrolling region."),
        [ViewWidget.Text] = ("none", "Text with a style token; a localisation key or literal."),
        [ViewWidget.Icon] = ("none", "Icon from a closed icon set. Needs a label."),
        [ViewWidget.Button] = ("none", "Calls a named action. Needs a label."),
        [ViewWidget.Input] = ("none", "Text input bound to a value; the write goes through an action. Needs a label."),
        [ViewWidget.List] = ("one template", "Keyed list over a list data source, with one item template."),
        [ViewWidget.Tabs] = ("tabs", "Named tabs. Its children are `tab` items, which are structural and not a widget."),
        [ViewWidget.Badge] = ("none", "Count or status chip."),
        [ViewWidget.Divider] = ("none", "Separator."),
        [ViewWidget.Placeholder] = ("none", "Failure card shown in place of something that could not render."),
    };

    private static readonly Dictionary<ViewActionKind, string> _actions = new()
    {
        [ViewActionKind.Navigate] = "Navigates to a pane or a GC. The target names the destination.",
        [ViewActionKind.Invoke] = "Runs a named command from the module manifest, through the same approval tier as the control it mirrors. A view cannot lower a tier.",
        [ViewActionKind.OpenPane] = "Opens a pane. The target names the pane.",
        [ViewActionKind.SetSetting] = "Sets a setting key the manifest declares. The target names the key.",
    };

    /// <summary>Generates the pages using the golden corpus embedded in this assembly.</summary>
    public static IReadOnlyList<RubedoReferencePage> Generate()
    {
        using Stream stream = typeof(RubedoReferenceGenerator).Assembly.GetManifestResourceStream(_corpusResource)
            ?? throw new InvalidOperationException($"The embedded corpus '{_corpusResource}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Generate(reader.ReadToEnd());
    }

    /// <summary>Generates the pages with worked examples taken from the given golden corpus text (the format of <c>rubedo-v1.json</c>).</summary>
    public static IReadOnlyList<RubedoReferencePage> Generate(string goldenCorpusJson)
    {
        ArgumentNullException.ThrowIfNull(goldenCorpusJson);
        IReadOnlyList<RubedoFunctionExample> corpus = ReadCorpus(goldenCorpusJson);

        var pages = new List<RubedoReferencePage>
        {
            new("index.md", Index()),
            new("identifiers.md", Identifiers()),
            new("functions.md", Functions(corpus)),
            new("operators.md", Operators()),
            new("limits.md", Limits()),
            new("widgets.md", Widgets()),
            new("actions.md", Actions()),
            new("diagnostics.md", Diagnostics()),
        };
        pages.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return pages;
    }

    private static string Index()
    {
        var page = new Page();
        page.Line("# Rubedo reference");
        page.Blank();
        page.Line("Generated from the engine by `bazaar-rubedo-docs` (doc 41d section 6). Do not edit by hand: change the language or the generator and regenerate.");
        page.Blank();
        page.Line($"Language version: {RubedoLanguage.Version}.");
        page.Blank();
        page.Line("- [Identifiers](identifiers.md): the typed context an expression can read.");
        page.Line("- [Functions](functions.md): the closed function table, each with a worked example.");
        page.Line("- [Operators and precedence](operators.md): the grammar and how operators bind.");
        page.Line("- [Limits](limits.md): the floors that bound compiling and evaluating.");
        page.Line("- [Widgets](widgets.md): the closed widget set of view documents.");
        page.Line("- [Actions](actions.md): the closed action set of view documents.");
        page.Line("- [Diagnostic codes](diagnostics.md): every stable code with a short explanation.");
        return page.ToString();
    }

    private static string Identifiers()
    {
        var page = new Page();
        page.Line("# Identifiers");
        page.Blank();
        page.Line("The host supplies every identifier on each evaluation. An unknown identifier is a compile error (`unknown-identifier`), and a missing value is a runtime error (`missing-value`), never a silent default.");
        page.Blank();
        page.Line("| Identifier | Type | Meaning |");
        page.Line("|---|---|---|");
        foreach (KeyValuePair<string, RubedoType> identifier in RubedoSchema.Standard.Identifiers.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            if (!_identifierMeanings.TryGetValue(identifier.Key, out string? meaning))
            {
                throw new InvalidOperationException($"The reference has no words for identifier '{identifier.Key}'.");
            }

            page.Line($"| <a id=\"id-{identifier.Key}\"></a>`{identifier.Key}` | {FunctionDocs.TypeName(identifier.Value)} | {Cell(meaning)} |");
        }

        return page.ToString();
    }

    private static string Functions(IReadOnlyList<RubedoFunctionExample> corpus)
    {
        var page = new Page();
        page.Line("# Functions");
        page.Blank();
        page.Line("The function table is closed: adding a function is a language version change. A call to any other name is a compile error (`unknown-function`).");
        foreach (RubedoFunctionSignature signature in RubedoFunctionTable.Signatures)
        {
            page.Blank();
            page.Line($"<a id=\"fn-{signature.Name}\"></a>");
            page.Line($"## {signature.Name}");
            page.Blank();
            page.Line($"`{FunctionDocs.Label(signature)}`");
            page.Blank();
            page.Line(FunctionDocs.Description(signature.Name));
            if (signature.IsVariadic)
            {
                page.Blank();
                page.Line($"The first argument is the value and the rest are candidates, one to {RubedoLanguage.MaxInSetCandidates.ToString(CultureInfo.InvariantCulture)} of them.");
            }

            page.Blank();
            RubedoFunctionExample? example = FindExample(corpus, signature.Name);
            if (example is null)
            {
                page.Line("No worked example in the golden corpus.");
                continue;
            }

            page.Line("Example (from the golden corpus):");
            page.Blank();
            page.Line("```");
            page.Line(example.Source);
            page.Line("```");
            page.Blank();
            string result = Render(example.Value);
            page.Line(example.Context.Count == 0
                ? $"With an empty context this evaluates to `{result}`."
                : $"With {string.Join(", ", example.Context.Select(c => $"`{c.Key} = {Render(c.Value)}`"))} this evaluates to `{result}`.");
        }

        return page.ToString();
    }

    private static string Operators()
    {
        var page = new Page();
        page.Line("# Operators and precedence");
        page.Blank();
        page.Line("Operators need exact types; there are no implicit conversions. Relational and equality operators do not chain, so `a < b < c` is a syntax error (`chained-comparison`). A relation under `==` or `!=` needs parentheses.");
        page.Blank();
        page.Line("## Precedence");
        page.Blank();
        page.Line("From loosest to tightest binding:");
        page.Blank();
        page.Line("| Level | Operators | Operands | Result |");
        page.Line("|---|---|---|---|");
        page.Line("| 1 | `\\|\\|` | bool, bool | bool, short-circuits |");
        page.Line("| 2 | `&&` | bool, bool | bool, short-circuits |");
        page.Line("| 3 | `==` `!=` | two operands of the same type | bool |");
        page.Line("| 4 | `<` `<=` `>` `>=` | number, number | bool |");
        page.Line("| 5 | `+` `-` | number, number | number |");
        page.Line("| 6 | `*` `/` `%` | number, number | number |");
        page.Line("| 7 | `!` | bool (prefix) | bool |");
        page.Line("| 7 | `-` | number (prefix) | number |");
        page.Line("| 8 | call `name(args)` | per the function table | per the function table |");
        page.Blank();
        page.Line("## Grammar");
        page.Blank();
        page.Line("```");
        page.Line("expr      := or");
        page.Line("or        := and ( \"||\" and )*");
        page.Line("and       := equality ( \"&&\" equality )*");
        page.Line("equality  := operand ( (\"==\" | \"!=\") operand )?");
        page.Line("relation  := sum ( (\"<\" | \"<=\" | \">\" | \">=\") sum )?");
        page.Line("operand   := sum | \"(\" relation \")\"   -- a relation under == or != needs parentheses");
        page.Line("sum       := product ( (\"+\" | \"-\") product )*");
        page.Line("product   := unary ( (\"*\" | \"/\" | \"%\") unary )*");
        page.Line("unary     := (\"!\" | \"-\") unary | call");
        page.Line("call      := primary ( \"(\" [ expr ( \",\" expr )* ] \")\" )?   -- only for identifiers in the function table");
        page.Line("primary   := number | string | \"true\" | \"false\" | identifier | \"(\" expr \")\"");
        page.Line("```");
        page.Blank();
        page.Line("## Literals");
        page.Blank();
        page.Line("| Type | Literal | Notes |");
        page.Line("|---|---|---|");
        page.Line("| bool | `true`, `false` | |");
        page.Line($"| number | `12`, `0.5`, `600` | Decimal, not binary float, so `0.1 + 0.2 == 0.3`. Range plus or minus {FormatNumber(RubedoLanguage.MaxMagnitude)}. Overflow is an error. |");
        page.Line($"| text | `\"phone\"` | At most {FormatNumber(RubedoOptions.Default.MaxStringChars)} characters, no escape sequences, so a `\"` cannot appear. |");
        return page.ToString();
    }

    private static string Limits()
    {
        RubedoOptions defaults = RubedoOptions.Default;
        var page = new Page();
        page.Line("# Limits");
        page.Blank();
        page.Line("These are floors. A caller may tighten them through `RubedoOptions`, never loosen them.");
        page.Blank();
        page.Line("| Limit | Default | Diagnostic |");
        page.Line("|---|---|---|");
        page.Line($"| Source length | {FormatNumber(defaults.MaxSourceChars)} characters | `too-long` |");
        page.Line($"| Tokens | {FormatNumber(defaults.MaxTokens)} | `too-many-tokens` |");
        page.Line($"| Nesting depth | {FormatNumber(defaults.MaxDepth)} | `too-deep` |");
        page.Line($"| Evaluation steps | {FormatNumber(defaults.MaxSteps)}, one per syntax node visited | `budget` |");
        page.Line($"| String literal | {FormatNumber(defaults.MaxStringChars)} characters | `string-too-long` |");
        page.Line($"| Number magnitude | {FormatNumber(RubedoLanguage.MaxMagnitude)} | `number-out-of-range` |");
        page.Line($"| `inSet` candidates | {FormatNumber(RubedoLanguage.MaxInSetCandidates)} after the first argument | `arity` |");
        return page.ToString();
    }

    private static string Widgets()
    {
        var page = new Page();
        page.Line("# Widgets");
        page.Blank();
        page.Line("The widget set of view documents is closed. A node names a widget by its wire name; an unknown name is `view.unknown_widget`.");
        page.Blank();
        page.Line("| Widget | Children | Role |");
        page.Line("|---|---|---|");
        foreach (ViewWidget widget in Enum.GetValues<ViewWidget>().OrderBy(w => ViewWidgets.WireName(w), StringComparer.Ordinal))
        {
            if (!_widgets.TryGetValue(widget, out (string Children, string Role) entry))
            {
                throw new InvalidOperationException($"The reference has no words for widget '{widget}'.");
            }

            string wire = ViewWidgets.WireName(widget);
            page.Line($"| <a id=\"widget-{wire}\"></a>`{wire}` | {entry.Children} | {Cell(entry.Role)} |");
        }

        return page.ToString();
    }

    private static string Actions()
    {
        var page = new Page();
        page.Line("# Actions");
        page.Blank();
        page.Line("The action set of view documents is closed, and no action sends a raw wire call. A button refers to a declared action by its id.");
        page.Blank();
        page.Line("| Action | Meaning |");
        page.Line("|---|---|");
        foreach (ViewActionKind kind in Enum.GetValues<ViewActionKind>().OrderBy(WireName, StringComparer.Ordinal))
        {
            if (!_actions.TryGetValue(kind, out string? meaning))
            {
                throw new InvalidOperationException($"The reference has no words for action '{kind}'.");
            }

            page.Line($"| <a id=\"action-{WireName(kind)}\"></a>`{WireName(kind)}` | {Cell(meaning)} |");
        }

        return page.ToString();
    }

    private static string Diagnostics()
    {
        var page = new Page();
        page.Line("# Diagnostic codes");
        page.Blank();
        page.Line("Codes are stable. The golden corpus asserts them, and tools key on them.");
        foreach (string area in new[] { "expression", "view" })
        {
            page.Blank();
            page.Line(area == "expression" ? "## Expression codes" : "## View document codes");
            page.Blank();
            page.Line("| Code | Meaning |");
            page.Line("|---|---|");
            foreach (RubedoDiagnosticEntry entry in RubedoDiagnosticCatalog.Entries.Where(e => e.Area == area))
            {
                page.Line($"| <a id=\"code-{entry.Code.Replace('.', '-').Replace('_', '-')}\"></a>`{entry.Code}` | {Cell(entry.Explanation)} |");
            }
        }

        return page.ToString();
    }

    /// <summary>The wire spelling of an action kind: the enum name in lower camel case (<c>OpenPane</c> becomes <c>openPane</c>).</summary>
    private static string WireName(ViewActionKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string FormatNumber(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// The worked example of every function: for each name in the function table, the first golden-corpus case that evaluates to
    /// a value and calls it, in corpus order. A function with no such case has no entry.
    /// </summary>
    public static IReadOnlyList<RubedoFunctionExample> GetExamples(string goldenCorpusJson)
    {
        ArgumentNullException.ThrowIfNull(goldenCorpusJson);
        IReadOnlyList<RubedoFunctionExample> corpus = ReadCorpus(goldenCorpusJson);
        return RubedoFunctionTable.Signatures
            .Select(s => FindExample(corpus, s.Name))
            .OfType<RubedoFunctionExample>()
            .ToList();
    }

    private static RubedoFunctionExample? FindExample(IReadOnlyList<RubedoFunctionExample> corpus, string function)
    {
        string call = function + "(";
        foreach (RubedoFunctionExample candidate in corpus)
        {
            int at = candidate.Source.IndexOf(call, StringComparison.Ordinal);
            if (at >= 0 && (at == 0 || !char.IsLetterOrDigit(candidate.Source[at - 1])))
            {
                return candidate with { Function = function };
            }
        }

        return null;
    }

    private static string Render(object value) => value switch
    {
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############", CultureInfo.InvariantCulture),
        _ => $"\"{value}\"",
    };

    private static List<RubedoFunctionExample> ReadCorpus(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var cases = new List<RubedoFunctionExample>();
        foreach (JsonElement element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            // Only cases that evaluate to a value can serve as an example.
            if (!element.GetProperty("expect").TryGetProperty("value", out JsonElement value))
            {
                continue;
            }

            var context = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.GetProperty("context").EnumerateObject())
            {
                context[property.Name] = ToValue(property.Value);
            }

            cases.Add(new RubedoFunctionExample(string.Empty, element.GetProperty("source").GetString()!, context, ToValue(value)));
        }

        return cases;
    }

    private static object ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDecimal(),
        _ => element.GetString()!,
    };

    /// <summary>Line builder that always uses LF and ends the page with exactly one newline.</summary>
    private sealed class Page
    {
        private readonly StringBuilder _text = new();

        public void Line(string text) => _text.Append(text).Append('\n');

        public void Blank() => _text.Append('\n');

        public override string ToString() => _text.ToString();
    }
}
