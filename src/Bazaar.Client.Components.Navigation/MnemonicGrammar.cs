namespace Bazaar.Client.Components.Navigation;

/// <summary>The verb a typed mnemonic carries. Only navigation is defined in v1.</summary>
public enum MnemonicVerb
{
    /// <summary>A named target or a command, typed on its own.</summary>
    Run,

    /// <summary>'n' followed by a short target code, such as <c>ngc1</c>. Moves focus to the target.</summary>
    Navigate,
}

/// <summary>
/// What a typed mnemonic will do, shown in the overlay before Enter runs it.
/// <see cref="Binding"/> is null when nothing matches.
/// </summary>
public sealed record MnemonicIntent(
    string Typed,
    MnemonicVerb Verb,
    string Code,
    MnemonicAssignment? Binding,
    string Explanation);

/// <summary>
/// Parses typed mnemonics against the effective assignments. Vim is the reference for the grammar
/// (doc 41, section 4.12): a single verb letter, then a short target code, with named targets typed
/// alone. Pure: it never executes anything, so the overlay can show the result before Enter.
/// </summary>
public static class MnemonicGrammar
{
    /// <summary>The verb letter for navigation. A mnemonic that starts with it and has a code is a navigation target.</summary>
    public const char NavigateVerb = 'n';

    public static MnemonicIntent Parse(string typed, IReadOnlyList<MnemonicAssignment> effective)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(effective);

        var binding = effective.FirstOrDefault(a => string.Equals(a.Mnemonic, typed, StringComparison.Ordinal));
        var isNavigate = typed.Length > 1 && typed[0] == NavigateVerb;
        var verb = isNavigate ? MnemonicVerb.Navigate : MnemonicVerb.Run;
        var code = isNavigate ? typed[1..] : typed;

        if (binding is null)
        {
            return new MnemonicIntent(typed, verb, code, null, $"No mnemonic '{typed}'.");
        }

        var prefix = verb == MnemonicVerb.Navigate ? "Move to" : "Run";
        return new MnemonicIntent(typed, verb, code, binding, $"{prefix}: {binding.Title}");
    }

    /// <summary>
    /// Mnemonics that begin with the typed text, for the which-key hints. Each hint carries its
    /// explanation so screen readers can read the choices out.
    /// </summary>
    public static IReadOnlyList<MnemonicAssignment> Candidates(string typed, IReadOnlyList<MnemonicAssignment> effective) =>
        effective.Where(a => a.Mnemonic.StartsWith(typed, StringComparison.Ordinal))
            .OrderBy(a => a.Mnemonic.Length)
            .ThenBy(a => a.Mnemonic, StringComparer.Ordinal)
            .ToList();
}
