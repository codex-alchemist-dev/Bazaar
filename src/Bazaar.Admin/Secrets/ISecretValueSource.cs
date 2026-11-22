namespace Bazaar.Admin;

/// <summary>
/// Where <c>secret set</c> reads the value from: standard input when it is redirected, otherwise a prompt that does not echo.
/// The value is never an argument. Returns null when no value could be read (input ended, or the operator cancelled).
/// </summary>
public interface ISecretValueSource
{
    /// <summary>Reads one value. <paramref name="prompt"/> names the reference only, and is shown on a terminal only.</summary>
    string? Read(string prompt);
}
