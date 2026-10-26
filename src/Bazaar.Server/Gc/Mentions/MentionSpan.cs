namespace Bazaar.Server.Gc.Mentions;

/// <summary>
/// A structured mention span a client sends with a message body. Offsets are measured in UTF-16
/// code units, the same units as <see cref="string.Length"/> and the client's text buffer, so an
/// emoji outside the Basic Multilingual Plane counts as two units. Plain "@text" that carries no
/// span is never a mention.
/// </summary>
/// <param name="Start">Zero-based UTF-16 offset of the first unit of the mention text.</param>
/// <param name="Length">Number of UTF-16 units the mention covers.</param>
/// <param name="PrincipalId">The stable principal id the client resolved the handle to.</param>
public sealed record MentionSpan(int Start, int Length, Guid PrincipalId);
