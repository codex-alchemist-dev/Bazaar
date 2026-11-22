namespace Bazaar.Server.Godot;

/// <summary>
/// Bounds Godot output before it is parsed. Output is untrusted and can be large; anything past the cap is dropped and a
/// marker line says so. The cap is in characters (UTF-16 code units), a bound on memory rather than bytes.
/// </summary>
internal static class GodotOutputCap
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
