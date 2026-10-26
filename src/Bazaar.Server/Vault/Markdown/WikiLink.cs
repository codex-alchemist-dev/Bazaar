namespace Bazaar.Server.Vault.Markdown;

/// <summary>Whether a wiki-link is a plain link or an embed (<c>![[...]]</c>).</summary>
public enum WikiLinkKind
{
    Link,
    Embed,
}

/// <summary>The kind of anchor after the target: none, a heading, or a block id.</summary>
public enum WikiLinkAnchorKind
{
    None,
    Heading,
    Block,
}

/// <summary>
/// A parsed <c>[[target#anchor|alias]]</c>. <see cref="Start"/> and <see cref="Length"/> are character offsets
/// into the whole source text (front matter included), covering the opening <c>[[</c> or <c>![[</c> through the closing <c>]]</c>.
/// </summary>
/// <param name="Target">Raw target text, e.g. <c>projects/x/Plan</c> or <c>Auth service</c>. Empty for same-document links such as <c>[[#Heading]]</c>.</param>
/// <param name="AnchorKind">Whether an anchor follows the target, and what kind.</param>
/// <param name="Anchor">Heading text (not slugged) or block id (without the leading <c>^</c>); null when <see cref="AnchorKind"/> is None.</param>
/// <param name="Alias">Display text after <c>|</c>; null when absent.</param>
public sealed record WikiLink(
    WikiLinkKind Kind,
    string Target,
    WikiLinkAnchorKind AnchorKind,
    string? Anchor,
    string? Alias,
    int Start,
    int Length);
