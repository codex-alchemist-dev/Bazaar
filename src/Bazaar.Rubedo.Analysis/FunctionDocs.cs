using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Analysis;

/// <summary>
/// Editor text for the closed function table (doc 41a section 5): parameter names and one-line meanings. The types come from
/// <see cref="RubedoFunctionTable"/>, so only the words live here.
/// </summary>
public static class FunctionDocs
{
    private static readonly Dictionary<string, (string[] Names, string Description)> _docs = new(StringComparer.Ordinal)
    {
        ["min"] = (["a", "b"], "The smaller of two numbers."),
        ["max"] = (["a", "b"], "The larger of two numbers."),
        ["clamp"] = (["value", "lo", "hi"], "Limits a number to the range lo to hi. An error when lo is above hi."),
        ["abs"] = (["value"], "The absolute value of a number."),
        ["contains"] = (["text", "part"], "True when text contains part."),
        ["startsWith"] = (["text", "prefix"], "True when text starts with prefix."),
        ["inSet"] = (["value", "candidate"], "True when value equals one of the candidates. Numbers or text, all of the same type."),
    };

    public static string TypeName(RubedoType type) => type switch
    {
        RubedoType.Bool => "bool",
        RubedoType.Number => "number",
        _ => "text",
    };

    public static string ParameterLabel(RubedoFunctionSignature signature, int index)
    {
        string name = _docs.TryGetValue(signature.Name, out var doc) && index < doc.Names.Length ? doc.Names[index] : $"arg{index + 1}";
        RubedoType? type = signature.Parameters[index];
        string typeName = type is { } t ? TypeName(t) : "number|text";
        string rest = signature.IsVariadic && index == signature.Parameters.Count - 1 ? "..." : string.Empty;
        return $"{name}{rest}: {typeName}";
    }

    public static string Label(RubedoFunctionSignature signature)
    {
        var parameters = Enumerable.Range(0, signature.Parameters.Count).Select(i => ParameterLabel(signature, i));
        return $"{signature.Name}({string.Join(", ", parameters)}): {TypeName(signature.Result)}";
    }

    public static string Description(string name) => _docs.TryGetValue(name, out var doc) ? doc.Description : string.Empty;
}
