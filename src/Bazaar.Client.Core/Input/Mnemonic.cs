namespace Bazaar.Client.Core.Input;

/// <summary>A mnemonic is typed text: letters, digits and Space. Ctrl, Alt and Meta are never allowed.</summary>
public static class Mnemonic
{
    /// <summary>Returns the first problem with the keys, or null when they form a valid mnemonic.</summary>
    public static string? Validate(IEnumerable<KeyStroke> keys)
    {
        var count = 0;
        foreach (var key in keys)
        {
            count++;
            if (key.HasChordModifier)
            {
                return "Ctrl, Alt and Meta are not allowed in mnemonics.";
            }

            if (key.Named != NamedKey.None || key.Character is not char c)
            {
                return "Mnemonics take letters, digits and Space only.";
            }

            if (!char.IsLetterOrDigit(c) && c != ' ')
            {
                return $"'{c}' is not a letter, digit or Space.";
            }
        }

        return count == 0 ? "A mnemonic needs at least one key." : null;
    }

    public static string? Validate(string text) => Validate(text.Select(c => KeyStroke.OfChar(c)));

    /// <summary>Converts validated keys to the text form the router matches on.</summary>
    public static string ToText(IEnumerable<KeyStroke> keys) =>
        new(keys.Select(key => key.Character ?? throw new ArgumentException("Mnemonic keys must be printable.", nameof(keys))).ToArray());
}
