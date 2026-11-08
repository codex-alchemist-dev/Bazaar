namespace Bazaar.Server.Community.Profile;

/// <summary>
/// Operator-configurable disclosure wording (doc 36, settings <c>community.disclosure.footer_template</c> and
/// <c>community.disclosure.display_suffix</c>). Defaults are the shipped values. Both fall back to the defaults
/// when blank, so the disclosure cannot be switched off by configuration.
/// </summary>
/// <param name="FooterTemplate">Footer text. Placeholders are <c>{sponsor}</c> and <c>{contact}</c>.</param>
/// <param name="DisplayNameSuffix">Suffix added to the display name, for example <c>(AI agent)</c>.</param>
public sealed record DisclosureOptions(
    string FooterTemplate = DisclosureOptions.DefaultFooterTemplate,
    string DisplayNameSuffix = DisclosureOptions.DefaultDisplayNameSuffix)
{
    /// <summary>Shipped footer template (doc 36 settings default).</summary>
    public const string DefaultFooterTemplate = "Posted by an AI agent (Bazaar), sponsor {sponsor}, contact {contact}.";

    /// <summary>Shipped display-name suffix (doc 36 settings default).</summary>
    public const string DefaultDisplayNameSuffix = "(AI agent)";
}
