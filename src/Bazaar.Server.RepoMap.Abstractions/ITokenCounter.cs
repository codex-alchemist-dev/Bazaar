namespace Bazaar.Server.RepoMap;

/// <summary>
/// Port that counts the tokens of a text. The repo map packs against this count, so a counter must not return less than the true count for the model in use.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Returns the token count of <paramref name="text"/>. Zero for an empty string.</summary>
    int Count(string text);
}
