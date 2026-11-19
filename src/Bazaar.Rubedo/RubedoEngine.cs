using Bazaar.Rubedo.Lexing;
using Bazaar.Rubedo.Runtime;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Syntax;

namespace Bazaar.Rubedo;

/// <summary>A compiled, checked expression. Immutable and safe to share between threads.</summary>
public sealed class RubedoProgram
{
    internal RubedoProgram(string source, Node root, RubedoType resultType, RubedoSchema schema, RubedoOptions options)
    {
        Source = source;
        Root = root;
        ResultType = resultType;
        Schema = schema;
        Options = options;
    }

    public string Source { get; }

    public RubedoType ResultType { get; }

    public RubedoSchema Schema { get; }

    internal Node Root { get; }

    internal RubedoOptions Options { get; }
}

/// <summary>Result of compiling. Program is null whenever Diagnostics is not empty.</summary>
public sealed record RubedoCompileResult(RubedoProgram? Program, IReadOnlyList<RubedoDiagnostic> Diagnostics)
{
    public bool IsValid => Program is not null;
}

/// <summary>Result of evaluating. Value is null when Error is set. Value is a bool, decimal or string.</summary>
public sealed record RubedoEvaluation(object? Value, RubedoDiagnostic? Error)
{
    public bool IsOk => Error is null;
}

/// <summary>
/// The Rubedo public API (doc 41a section 7). Compile and Evaluate never throw on bad input: problems come back as diagnostics.
/// The server and every head call this same code, so results agree everywhere (doc 41a section 8).
/// </summary>
public static class RubedoEngine
{
    /// <summary>Compiles source against a schema. When <paramref name="requiredType"/> is set, the result must have that type.</summary>
    public static RubedoCompileResult Compile(string source, RubedoSchema? schema = null, RubedoType? requiredType = null, RubedoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        schema ??= RubedoSchema.Standard;
        options = (options ?? RubedoOptions.Default).Clamped();

        if (source.Length > options.MaxSourceChars)
        {
            return Fail("too-long", $"An expression may have at most {options.MaxSourceChars} characters.", options.MaxSourceChars);
        }

        try
        {
            List<Token> tokens = Lexer.Tokenize(source, options);
            Node root = new Parser(tokens, options).ParseExpression();
            RubedoType type = TypeChecker.Check(root, schema);
            if (requiredType is { } required && type != required)
            {
                return Fail("result-type", $"The expression must produce {required}, not {type}.", root.Offset);
            }

            return new RubedoCompileResult(new RubedoProgram(source, root, type, schema, options), []);
        }
        catch (RubedoException ex)
        {
            return new RubedoCompileResult(null, [ex.ToDiagnostic()]);
        }
    }

    /// <summary>Evaluates a compiled program. Each identifier must be supplied with the type the schema declares.</summary>
    public static RubedoEvaluation Evaluate(RubedoProgram program, IReadOnlyDictionary<string, object> context)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(context);

        foreach (KeyValuePair<string, RubedoType> declared in program.Schema.Identifiers)
        {
            if (!context.TryGetValue(declared.Key, out object? value) || value is null)
            {
                continue;
            }

            bool matches = declared.Value switch
            {
                RubedoType.Bool => value is bool,
                RubedoType.Number => value is decimal,
                _ => value is string,
            };
            if (!matches)
            {
                return new RubedoEvaluation(null, new RubedoDiagnostic("context-type", $"'{declared.Key}' must be a {declared.Value}."));
            }
        }

        try
        {
            object value = new Evaluator(context, program.Options.MaxSteps).Evaluate(program.Root);
            return new RubedoEvaluation(value, null);
        }
        catch (RubedoException ex)
        {
            return new RubedoEvaluation(null, ex.ToDiagnostic());
        }
    }

    /// <summary>
    /// A visibility rule (doc 41a section 7, decision 4 of the owner): the result must be true to show. An error hides the pane
    /// and carries a notice for the layout issue list (fail closed).
    /// </summary>
    public static RubedoVisibility IsVisible(RubedoProgram program, IReadOnlyDictionary<string, object> context)
    {
        if (program.ResultType != RubedoType.Bool)
        {
            return new RubedoVisibility(false, new RubedoDiagnostic("result-type", "A visibility rule must produce true or false."));
        }

        RubedoEvaluation result = Evaluate(program, context);
        if (!result.IsOk)
        {
            return new RubedoVisibility(false, result.Error);
        }

        return new RubedoVisibility((bool)result.Value!, null);
    }

    private static RubedoCompileResult Fail(string code, string message, int offset) =>
        new(null, [new RubedoDiagnostic(code, message, offset)]);
}

/// <summary>Visibility decision. Notice is set when the rule failed and the pane was hidden.</summary>
public sealed record RubedoVisibility(bool Visible, RubedoDiagnostic? Notice);
