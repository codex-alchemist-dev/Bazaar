namespace Bazaar.Rubedo;

/// <summary>The three value types of Rubedo (doc 41a section 2). There are no implicit conversions.</summary>
public enum RubedoType
{
    Bool,
    Number,
    Text,
}

/// <summary>
/// Language constants (doc 41a sections 2, 5 and 6). Limits are floors: callers may tighten them through
/// <see cref="RubedoOptions"/>, never loosen them.
/// </summary>
public static class RubedoLanguage
{
    /// <summary>Working name. Clearance is open (doc 41 section 10). One constant, so a rename is one edit.</summary>
    public const string Name = "Rubedo";

    /// <summary>Version of the language. Documents carry <c>dslVersion</c>; an unknown version is rejected on load.</summary>
    public const int Version = 1;

    /// <summary>Largest magnitude a number may take at any point (doc 41a section 2: ±10^15).</summary>
    public const decimal MaxMagnitude = 1_000_000_000_000_000m;

    /// <summary>Largest number of arguments <c>inSet</c> accepts after its first argument (doc 41a section 5).</summary>
    public const int MaxInSetCandidates = 16;
}

/// <summary>The rules that bound compiling and evaluating (doc 41a section 6). Defaults are the floors.</summary>
/// <param name="MaxSourceChars">Source length in characters.</param>
/// <param name="MaxTokens">Tokens in one expression.</param>
/// <param name="MaxDepth">Nesting depth of parentheses, unary operators and call arguments.</param>
/// <param name="MaxSteps">Evaluation steps: one per syntax node visited.</param>
/// <param name="MaxStringChars">String literal length.</param>
public sealed record RubedoOptions(
    int MaxSourceChars = 512,
    int MaxTokens = 256,
    int MaxDepth = 32,
    int MaxSteps = 1000,
    int MaxStringChars = 64)
{
    /// <summary>Returns a copy whose limits are no looser than the floors. Callers may tighten, never loosen.</summary>
    public RubedoOptions Clamped() => new(
        Math.Min(MaxSourceChars, 512),
        Math.Min(MaxTokens, 256),
        Math.Min(MaxDepth, 32),
        Math.Min(MaxSteps, 1000),
        Math.Min(MaxStringChars, 64));

    public static RubedoOptions Default { get; } = new();
}
