using Markdig.Syntax.Inlines;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>A Markdig leaf node for one <c>[[...]]</c> or <c>![[...]]</c>, carrying the parsed link and its source span.</summary>
internal sealed class WikiLinkInline(WikiLink link) : LeafInline
{
    public WikiLink Link { get; } = link;
}
