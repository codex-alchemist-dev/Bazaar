namespace Bazaar.Server.Community.GitHub;

/// <summary>
/// Decides whether an outbound body carries the AI-disclosure footer (docs/design/36, setting <c>community.disclosure.footer_template</c>).
/// The footer text is operator-configurable, so the adapter asks this seam instead of hardcoding it.
/// </summary>
public interface ICommunityFooterCheck
{
    /// <summary>True when <paramref name="body"/> carries the disclosure footer.</summary>
    bool HasDisclosureFooter(string body);
}

/// <summary>Default check: the body contains the marker text of the shipped footer template.</summary>
public sealed class DefaultCommunityFooterCheck : ICommunityFooterCheck
{
    /// <summary>The fixed prefix of the shipped footer template (<c>Posted by an AI agent (Bazaar), sponsor ...</c>).</summary>
    public const string Marker = "Posted by an AI agent";

    public bool HasDisclosureFooter(string body) => body.Contains(Marker, StringComparison.Ordinal);
}
