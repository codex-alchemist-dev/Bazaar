using System.Collections.Specialized;

namespace Bazaar.Client.Chat.Abstractions;

/// <summary>One reaction chip on a message: the glyph, how many members reacted and whether this client's member is one of them.</summary>
/// <param name="Emoji">The reaction glyph. Chips on one message are keyed by it.</param>
/// <param name="Count">Members who reacted with it. A chip with a count of zero is not shown.</param>
/// <param name="Mine">True when this client's member reacted.</param>
public sealed record ChatReaction(string Emoji, int Count, bool Mine);

/// <summary>
/// Where reaction state comes from. The wire carries no reaction record yet (docs/design/01-gc-data-model.md names only software
/// reactions), so the chat takes the state through this seam and a head supplies the real source once the doc fixes the record.
/// </summary>
public interface IChatReactionSource
{
    /// <summary>The reactions on the message with sequence number <paramref name="n"/>, in the order the chips show.</summary>
    IReadOnlyList<ChatReaction> Reactions(long n);

    /// <summary>Raised with the sequence number of a message whose reactions changed.</summary>
    event EventHandler<long>? Changed;
}

/// <summary>
/// Who is typing in the chat. The wire has a presence stream but no typing signal documented yet, so this is the seam; a head wires
/// the real signal when the doc fixes it.
/// </summary>
public interface IChatTypingSource
{
    /// <summary>Display names of members typing now, in the order they started.</summary>
    IReadOnlyList<string> Typers { get; }

    /// <summary>Raised when <see cref="Typers"/> changed.</summary>
    event EventHandler? Changed;
}

/// <summary>A list of reactions a view can watch: the shape <see cref="ChatLine.Reactions"/> takes while it is live.</summary>
public interface IReactionCollection : IReadOnlyList<ChatReaction>, INotifyCollectionChanged;
