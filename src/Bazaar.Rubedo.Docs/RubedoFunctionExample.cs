namespace Bazaar.Rubedo.Docs;

/// <summary>
/// A worked example taken from the golden corpus: <see cref="Source"/> evaluated against <see cref="Context"/> gives <see cref="Value"/>
/// (a <see cref="bool"/>, <see cref="decimal"/> or <see cref="string"/>, as the engine's context and result use them).
/// </summary>
public sealed record RubedoFunctionExample(string Function, string Source, IReadOnlyDictionary<string, object> Context, object Value);
