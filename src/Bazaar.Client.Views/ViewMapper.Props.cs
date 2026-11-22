using System.Globalization;
using Avalonia.Controls;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

public sealed partial class ViewMapper
{
    /// <summary>The raw prop as the document wrote it: a literal (string, bool or number) or a <see cref="ViewExpr"/>.</summary>
    private static object? Required(ViewNode node, string name) =>
        node.Props.TryGetValue(name, out object? value)
            ? value
            : throw new ViewDeclarationException($"needs prop '{name}'.");

    /// <summary>
    /// Resolves a prop against the scope. A literal is itself. A bare identifier in scope reads that field (the document data,
    /// or one list item). Any other expression is compiled against the document's declared data and evaluated with Rubedo.
    /// A failure throws, so the node fails closed.
    /// </summary>
    private static object? Resolve(object? raw, Scope scope)
    {
        switch (raw)
        {
            case ViewExpr expr when scope.Values.TryGetValue(expr.Source, out object? field):
                return field;
            case ViewExpr expr:
                return Evaluate(expr, scope);
            case null or string or bool or double or decimal or int:
                return raw;
            default:
                throw new ViewDeclarationException("has an unknown prop value.");
        }
    }

    private static object Evaluate(ViewExpr expr, Scope scope)
    {
        RubedoCompileResult compiled = RubedoEngine.Compile(expr.Source, scope.Schema);
        if (compiled.Program is null)
        {
            throw new ViewDeclarationException(compiled.Diagnostics[0].Message);
        }

        var context = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((string name, object? value) in scope.Values)
        {
            if (value is not null)
            {
                context[name] = value;
            }
        }

        RubedoEvaluation result = RubedoEngine.Evaluate(compiled.Program, context);
        return result.IsOk ? result.Value! : throw new ViewDeclarationException(result.Error!.Message);
    }

    /// <summary>
    /// The Rubedo context at the top of a document: the standard identifiers plus the document's scalar data. A list source has
    /// no scalar type; its fields come into scope inside the list's template (see the list mapping).
    /// </summary>
    internal static RubedoSchema SchemaOf(ViewDocument document)
    {
        var identifiers = new Dictionary<string, RubedoType>(RubedoSchema.Standard.Identifiers, StringComparer.Ordinal);
        foreach ((string name, ViewDataSource source) in document.Data)
        {
            // The validator
            // reports a data name that shadows a context identifier; here the first declaration wins.
            if (source.Type is { } type)
            {
                identifiers.TryAdd(name, type);
            }
        }

        return new RubedoSchema(identifiers);
    }

    /// <summary>A prop as display text. Required props must not be empty.</summary>
    private static string Text(ViewNode node, string name, Scope scope) =>
        ToText(Resolve(Required(node, name), scope), name);

    /// <summary>
    /// A label or title prop: a localisation key or a literal (doc 41c, section 7). The localiser returns an unknown key
    /// unchanged, so a literal shows as written. Only labels and titles go through here: a bound <c>value</c> is never a key.
    /// </summary>
    private string Label(ViewNode node, string name, Scope scope) =>
        _context.Localizer.Text(Text(node, name, scope));

    /// <summary>
    /// The display text of a <c>text</c> or <c>badge</c> (doc 41c, section 3). A <c>value</c> is a literal or an expression and is
    /// shown as written, never looked up as a key. Without a <c>value</c>, a <c>label</c> is a localisation key or a literal.
    /// </summary>
    private string Content(ViewNode node, Scope scope)
    {
        if (node.Props.ContainsKey("value"))
        {
            return Text(node, "value", scope);
        }

        return node.Props.ContainsKey("label")
            ? Label(node, "label", scope)
            : throw new ViewDeclarationException("needs a 'value' or a 'label' prop.");
    }

    private static string? OptionalText(ViewNode node, string name, Scope scope) =>
        node.Props.ContainsKey(name) ? Text(node, name, scope) : null;

    private static string ToText(object? value, string name) => value switch
    {
        null => throw new ViewDeclarationException($"prop '{name}' has no value."),
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ViewDeclarationException($"prop '{name}' must be text."),
    };

    /// <summary>A prop that must be a literal string, for layout and token names that are fixed in the declaration.</summary>
    private static string LiteralString(ViewNode node, string name) =>
        Required(node, name) is string text
            ? text
            : throw new ViewDeclarationException($"prop '{name}' must be a literal string.");

    private static string OptionalLiteral(ViewNode node, string name, string fallback) =>
        node.Props.ContainsKey(name) ? LiteralString(node, name) : fallback;

    private static bool OptionalFlag(ViewNode node, string name, bool fallback)
    {
        if (!node.Props.TryGetValue(name, out object? value))
        {
            return fallback;
        }

        return value is bool flag
            ? flag
            : throw new ViewDeclarationException($"prop '{name}' must be a literal bool.");
    }

    private static double LiteralNumber(ViewNode node, string name, double min, double max)
    {
        if (AsNumber(Required(node, name)) is not double number || number < min || number > max)
        {
            throw new ViewDeclarationException($"prop '{name}' must be a number from {min} to {max}.");
        }

        return number;
    }

    private static double? AsNumber(object? value) => value switch
    {
        double number => number,
        decimal number => (double)number,
        int number => number,
        _ => null,
    };

    /// <summary>The children of a node, mapped in declaration order.</summary>
    private void AddChildren(Controls target, ViewNode node, Scope scope)
    {
        foreach (ViewNode child in node.Children)
        {
            target.Add(Node(child, scope));
        }
    }

    /// <summary>The action a button or input refers to, looked up by id in the document. An action with no target fails closed.</summary>
    private static ViewAction ActionOf(ViewNode node, string prop, Scope scope)
    {
        string id = LiteralString(node, prop);
        if (!scope.Document.Actions.TryGetValue(id, out ViewAction? action))
        {
            throw new ViewDeclarationException($"refers to unknown action '{id}'.");
        }

        return string.IsNullOrEmpty(action.Target)
            ? throw new ViewDeclarationException($"action '{id}' has no target.")
            : action;
    }
}
