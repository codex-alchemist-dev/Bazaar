namespace Bazaar.Ex;

/// <summary>
/// Operator-configurable behaviour of the navigation grammar (doc 41, section 4.12). Every part is on by default and can be
/// tightened: counts can be turned off or capped, the verb letters can be changed, and the AI-suggest key can be turned off.
/// The grammar stays syntax-first; none of these options lets anything run without Enter.
/// </summary>
public sealed record MnemonicOptions
{
    /// <summary>The default verb letters: <c>n</c> and <c>m</c> both navigate (doc 41 names <c>m</c> as move or navigate and uses <c>n</c> in <c>ngc1</c>).</summary>
    public static IReadOnlyDictionary<char, MnemonicVerb> DefaultVerbs { get; } =
        new Dictionary<char, MnemonicVerb> { ['n'] = MnemonicVerb.Navigate, ['m'] = MnemonicVerb.Navigate };

    /// <summary>The shipped defaults.</summary>
    public static MnemonicOptions Default { get; } = new();

    /// <summary>When true, digits typed before a command are a count, as in Vim (<c>3ngc</c>). A mnemonic that itself starts with those digits still wins.</summary>
    public bool CountsEnabled { get; init; } = true;

    /// <summary>The largest count accepted. A larger one is clamped to this value.</summary>
    public int MaxCount { get; init; } = 999;

    /// <summary>When true, Shift+Enter asks for AI suggestions for the typed text. The suggestions are proposals only; nothing runs.</summary>
    public bool AiSuggestEnabled { get; init; } = true;

    /// <summary>The single letters that start a compound command, and the verb each one means.</summary>
    public IReadOnlyDictionary<char, MnemonicVerb> Verbs { get; init; } = DefaultVerbs;
}
