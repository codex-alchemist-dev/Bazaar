namespace Bazaar.Ex;

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
    string Explanation,
    int Count = 1);

/// <summary>
/// Parses typed mnemonics against the effective assignments. Vim is the reference for the grammar
/// (doc 41, section 4.12): a single verb letter, then a short target code, with named targets typed
/// alone. Pure: it never executes anything, so the overlay can show the result before Enter.
/// </summary>
public static class MnemonicGrammar
{
    /// <summary>The verb letter for navigation. A mnemonic that starts with it and has a code is a navigation target.</summary>
    public const char NavigateVerb = 'n';

    public static MnemonicIntent Parse(string typed, IReadOnlyList<MnemonicAssignment> effective) =>
        Parse(typed, effective, MnemonicOptions.Default);

    public static MnemonicIntent Parse(string typed, IReadOnlyList<MnemonicAssignment> effective, MnemonicOptions options)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(options);

        var exact = effective.FirstOrDefault(a => string.Equals(a.Mnemonic, typed, StringComparison.Ordinal));
        var count = 1;
        var body = typed;
        if (exact is null && options.CountsEnabled && TrySplitCount(typed, options.MaxCount, out var split, out var rest))
        {
            count = split;
            body = rest;
        }

        var binding = body == typed
            ? exact
            : effective.FirstOrDefault(a => string.Equals(a.Mnemonic, body, StringComparison.Ordinal));
        var isCompound = body.Length > 1 && options.Verbs.ContainsKey(body[0]);
        var verb = isCompound ? options.Verbs[body[0]] : MnemonicVerb.Run;
        var code = isCompound ? body[1..] : body;
        var times = count > 1 ? $" (x{count})" : string.Empty;

        if (binding is null)
        {
            return new MnemonicIntent(typed, verb, code, null, $"No mnemonic '{body}'.", count);
        }

        var prefix = verb == MnemonicVerb.Navigate ? "Move to" : "Run";
        return new MnemonicIntent(typed, verb, code, binding, $"{prefix}: {binding.Title}{times}", count);
    }

    /// <summary>Splits leading digits off as a count. A count needs digits and then at least one more key; zero is not a count.</summary>
    private static bool TrySplitCount(string typed, int maxCount, out int count, out string rest)
    {
        var digits = 0;
        while (digits < typed.Length && char.IsAsciiDigit(typed[digits]))
        {
            digits++;
        }

        count = 1;
        rest = typed;
        if (digits == 0 || digits == typed.Length || typed[0] == '0')
        {
            return false;
        }

        var parsed = int.TryParse(typed.AsSpan(0, digits), out var value) ? value : maxCount;
        count = Math.Clamp(parsed, 1, Math.Max(1, maxCount));
        rest = typed[digits..];
        return true;
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
