namespace Bazaar.Rubedo.Semantics;

/// <summary>
/// The closed function table (doc 41a section 5). Adding a function is a language version change. <c>inSet</c> is generic
/// and is handled by the checker, so it is not listed here.
/// </summary>
internal static class Functions
{
    public static readonly IReadOnlyDictionary<string, FunctionSpec> All = new Dictionary<string, FunctionSpec>(StringComparer.Ordinal)
    {
        ["min"] = new([RubedoType.Number, RubedoType.Number], RubedoType.Number),
        ["max"] = new([RubedoType.Number, RubedoType.Number], RubedoType.Number),
        ["clamp"] = new([RubedoType.Number, RubedoType.Number, RubedoType.Number], RubedoType.Number),
        ["abs"] = new([RubedoType.Number], RubedoType.Number),
        ["contains"] = new([RubedoType.Text, RubedoType.Text], RubedoType.Bool),
        ["startsWith"] = new([RubedoType.Text, RubedoType.Text], RubedoType.Bool),
    };

    public const string InSet = "inSet";
}

/// <summary>Parameter and result types of one function.</summary>
internal sealed record FunctionSpec(RubedoType[] Parameters, RubedoType Result);
