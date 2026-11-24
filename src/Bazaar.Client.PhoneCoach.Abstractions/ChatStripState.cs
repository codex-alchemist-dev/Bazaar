namespace Bazaar.Client.PhoneCoach;

/// <summary>
/// What the top chat-name strip shows (doc 93: chat names, the current one highlighted, tappable; a peek of the neighbours and a swipe hint).
/// </summary>
/// <param name="Names">Every page's name, in order.</param>
/// <param name="Selected">Index of the current page, or -1 when there are none.</param>
public sealed record ChatStripState(IReadOnlyList<string> Names, int Selected)
{
    public static ChatStripState Empty { get; } = new([], -1);

    /// <summary>The current page's name, or empty.</summary>
    public string Current => Selected >= 0 && Selected < Names.Count ? Names[Selected] : "";

    /// <summary>The previous page's name, or null on the first page. Shown as a partial peek at the left edge.</summary>
    public string? Previous => Selected > 0 && Selected <= Names.Count ? Names[Selected - 1] : null;

    /// <summary>The next page's name, or null on the last page. Shown as a partial peek at the right edge.</summary>
    public string? Next => Selected >= 0 && Selected + 1 < Names.Count ? Names[Selected + 1] : null;

    /// <summary>True when a swipe has somewhere to go, so the swipe hint is worth showing.</summary>
    public bool HasNeighbours => Previous is not null || Next is not null;

    /// <summary>The text a screen reader says for the strip: the name and the position.</summary>
    public string Description => Names.Count == 0 || Selected < 0 ? "" : $"{Current}, chat {Selected + 1} of {Names.Count}";
}
