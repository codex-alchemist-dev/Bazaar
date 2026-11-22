using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Rules;

/// <summary>Outcome of one rule. Issue is set when the rule failed and Visible is false (fail closed, doc 41a section 7).</summary>
public sealed record VisibilityOutcome(bool Visible, RubedoDiagnostic? Issue);

/// <summary>
/// A visibility rule compiled once against the standard schema; it must produce true or false. Immutable and safe to share
/// between threads. Evaluating never throws: a runtime error hides the node and reports an issue code.
/// </summary>
public sealed class VisibilityRule
{
    private readonly RubedoProgram _program;

    private VisibilityRule(RubedoProgram program) => _program = program;

    public string Source => _program.Source;

    /// <summary>Compiles source. The rule is null whenever diagnostics is not empty.</summary>
    public static (VisibilityRule? Rule, IReadOnlyList<RubedoDiagnostic> Diagnostics) Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        RubedoCompileResult result = RubedoEngine.Compile(source, RubedoSchema.Standard, RubedoType.Bool);
        return result.Program is { } program ? (new VisibilityRule(program), []) : (null, result.Diagnostics);
    }

    public VisibilityOutcome Evaluate(RubedoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RubedoVisibility visibility = RubedoEngine.IsVisible(_program, context.ToValues());
        return new VisibilityOutcome(visibility.Visible, visibility.Notice);
    }
}
