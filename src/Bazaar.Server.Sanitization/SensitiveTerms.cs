namespace Bazaar.Server.Sanitization;

/// <summary>
/// The names and quoted passages the caller wants removed. Blank entries are ignored; null collections are rejected.
/// </summary>
/// <param name="People">Person names, matched as whole words.</param>
/// <param name="Agents">Agent names, matched as whole words.</param>
/// <param name="Repositories">Repository names, matched as whole words.</param>
/// <param name="Customers">Customer names, matched as whole words.</param>
/// <param name="ExternalQuotes">Literal external passages, matched anywhere.</param>
public sealed record SensitiveTerms(
    IEnumerable<string> People,
    IEnumerable<string> Agents,
    IEnumerable<string> Repositories,
    IEnumerable<string> Customers,
    IEnumerable<string> ExternalQuotes)
{
    /// <summary>Terms with nothing to remove.</summary>
    public static SensitiveTerms Empty { get; } = new([], [], [], [], []);
}
