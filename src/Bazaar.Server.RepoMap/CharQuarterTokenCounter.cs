namespace Bazaar.Server.RepoMap;

/// <summary>
/// Default token estimate: one token per four characters, rounded up. A rough count for English and code, used when no model tokenizer is configured.
/// </summary>
public sealed class CharQuarterTokenCounter : ITokenCounter
{
    /// <inheritdoc />
    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3) / 4;
    }
}
