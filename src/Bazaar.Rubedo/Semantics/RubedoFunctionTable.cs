namespace Bazaar.Rubedo.Semantics;

/// <summary>
/// One entry of the closed function table (doc 41a section 5), as tools see it. A <c>null</c> parameter type means "number or
/// text, the same as the other generic parameters" and is used only by <c>inSet</c>.
/// </summary>
/// <param name="Name">The function name as written in source.</param>
/// <param name="Parameters">Parameter types in order. For a variadic function, the last one repeats.</param>
/// <param name="Result">The result type.</param>
/// <param name="IsVariadic">True when the last parameter may repeat (up to <see cref="RubedoLanguage.MaxInSetCandidates"/> times).</param>
public sealed record RubedoFunctionSignature(string Name, IReadOnlyList<RubedoType?> Parameters, RubedoType Result, bool IsVariadic);

/// <summary>Read-only view of the function table the type checker uses, so editor tools can never drift from the language.</summary>
public static class RubedoFunctionTable
{
    /// <summary>Every function, sorted by name (ordinal), including <c>inSet</c>.</summary>
    public static IReadOnlyList<RubedoFunctionSignature> Signatures { get; } = Build();

    private static List<RubedoFunctionSignature> Build()
    {
        var list = new List<RubedoFunctionSignature>();
        foreach (KeyValuePair<string, FunctionSpec> entry in Functions.All)
        {
            list.Add(new RubedoFunctionSignature(entry.Key, entry.Value.Parameters.Select(p => (RubedoType?)p).ToArray(), entry.Value.Result, false));
        }

        list.Add(new RubedoFunctionSignature(Functions.InSet, [null, null], RubedoType.Bool, true));
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }
}
