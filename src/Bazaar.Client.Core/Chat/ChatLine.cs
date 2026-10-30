namespace Bazaar.Client.Core.Chat;

/// <summary>One message as a chat view shows it. Immutable; built by <see cref="ChatTimeline"/>.</summary>
/// <param name="N">The message's log sequence number.</param>
/// <param name="AuthorId">Principal who wrote the message.</param>
/// <param name="Author">Display label, with the agent marker when it applies.</param>
/// <param name="Time">Local time as <c>HH:mm</c>.</param>
/// <param name="Text">Text of the current version.</param>
/// <param name="Edited">True when the message has more than one version.</param>
/// <param name="Own">True when this client sent the message.</param>
public sealed record ChatLine(long N, Guid AuthorId, string Author, string Time, string Text, bool Edited, bool Own)
{
    /// <summary>The header above the text, such as <c>[09:14] Dom-tron (edited)</c>.</summary>
    public string Header => Edited ? $"[{Time}] {Author} (edited)" : $"[{Time}] {Author}";
}
