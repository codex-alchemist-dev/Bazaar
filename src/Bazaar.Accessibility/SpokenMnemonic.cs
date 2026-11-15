namespace Bazaar.Accessibility;

/// <summary>
/// Turns a mnemonic such as <c>ngc1</c> into the words a screen reader should speak (doc 41 section 4.7: every
/// mnemonic is also a spoken name, and the palette announces the command and its mnemonic in one line). Pure.
/// </summary>
public static class SpokenMnemonic
{
    /// <summary>
    /// Spells a mnemonic one character at a time, separated by spaces: <c>ngc1</c> becomes <c>N G C 1</c>. Letters
    /// are upper-cased so a screen reader reads them as letters, not as a word. Throws for an empty mnemonic or one
    /// with characters other than ASCII letters and digits, because a mnemonic can only be typed that way.
    /// </summary>
    public static string Speak(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);

        var parts = new string[mnemonic.Length];
        for (int i = 0; i < mnemonic.Length; i++)
        {
            char c = mnemonic[i];
            if (c is >= 'a' and <= 'z')
            {
                parts[i] = char.ToUpperInvariant(c).ToString();
            }
            else if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                parts[i] = c.ToString();
            }
            else
            {
                throw new ArgumentException($"Mnemonic '{mnemonic}' has a character that cannot be typed.", nameof(mnemonic));
            }
        }

        return string.Join(' ', parts);
    }
}

/// <summary>
/// The line a palette or list row speaks when it gets focus: the command's accessible name, then its mnemonic.
/// Example: <c>Constellation 2D, mnemonic N G C 2</c>. Pure; heads pass the result to their accessibility layer.
/// </summary>
public static class CommandAnnouncement
{
    /// <summary>Formats the announcement. Throws when the label is not a usable accessible name.</summary>
    public static string Format(string label, string mnemonic)
    {
        string? problem = AccessibleName.Problem(label);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(label));
        }

        return $"{label.Trim()}, mnemonic {SpokenMnemonic.Speak(mnemonic)}";
    }
}
