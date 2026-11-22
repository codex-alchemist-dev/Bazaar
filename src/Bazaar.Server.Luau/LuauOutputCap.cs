namespace Bazaar.Server.Luau;

/// <summary>
/// Bounds tool output before it is parsed. Tool output is untrusted and can be large; anything past the cap is dropped
/// and a marker line says so. The cap is in characters (UTF-16 code units), a bound on memory rather than bytes.
/// </summary>
internal static class LuauOutputCap
{
    /// <summary>Returns the text unchanged when within the cap; otherwise the first <paramref name="maxCharacters"/> characters plus a marker.</summary>
    public static string Apply(string text, int maxCharacters, out bool truncated)
    {
        if (text.Length <= maxCharacters)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        return text[..maxCharacters] + "\n[output truncated at " + maxCharacters + " characters]";
    }
}
