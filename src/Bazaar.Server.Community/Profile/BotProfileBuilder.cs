using System.Text.RegularExpressions;

namespace Bazaar.Server.Community.Profile;

/// <summary>
/// Renders the disclosure parts of a bot profile: display name, footer and profile validation (doc 36, "Bot profile conventions").
/// Agents never claim to be human, and the footer is always present.
/// </summary>
public static partial class BotProfileBuilder
{
    /// <summary>Display name with the AI-agent suffix. The suffix is added once and never dropped.</summary>
    public static string DisplayName(BotProfile profile, DisclosureOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var suffix = string.IsNullOrWhiteSpace(options.DisplayNameSuffix)
            ? DisclosureOptions.DefaultDisplayNameSuffix
            : options.DisplayNameSuffix.Trim();
        var name = profile.DisplayName.Trim();
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return name;
        return name.Length == 0 ? suffix : $"{name} {suffix}";
    }

    /// <summary>
    /// One-line disclosure footer. A blank template, or one with neither placeholder, falls back to the shipped default.
    /// Placeholder values are substituted in one pass, and line breaks are flattened so the footer stays one line.
    /// </summary>
    public static string Footer(BotProfile profile, DisclosureOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var template = options.FooterTemplate;
        if (string.IsNullOrWhiteSpace(template) || !PlaceholderPattern().IsMatch(template))
            template = DisclosureOptions.DefaultFooterTemplate;

        var rendered = PlaceholderPattern().Replace(template, match => match.Groups["name"].Value switch
        {
            "sponsor" => profile.SponsorName,
            _ => profile.Contact,
        });
        return rendered.ReplaceLineEndings(" ").Trim();
    }

    /// <summary>
    /// Body followed by a blank line and the footer. If the footer already ends the body, the body is returned
    /// unchanged, so applying this twice does not duplicate the footer.
    /// </summary>
    public static string AppendFooter(string body, BotProfile profile, DisclosureOptions options)
    {
        ArgumentNullException.ThrowIfNull(body);
        var footer = Footer(profile, options);
        var trimmed = body.TrimEnd();
        if (trimmed.EndsWith(footer, StringComparison.Ordinal))
            return body;
        return trimmed.Length == 0 ? footer : $"{trimmed}\n\n{footer}";
    }

    /// <summary>
    /// Checks the required profile fields. Returns the problems found; an empty list means the profile is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(BotProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.AgentId))
            problems.Add("AgentId is required.");
        if (string.IsNullOrWhiteSpace(profile.Platform))
            problems.Add("Platform is required.");
        if (string.IsNullOrWhiteSpace(profile.SponsorName))
            problems.Add("SponsorName is required.");
        if (string.IsNullOrWhiteSpace(profile.Contact))
            problems.Add("Contact is required.");
        if (profile.DisclosureUrl is null
            || !profile.DisclosureUrl.IsAbsoluteUri
            || profile.DisclosureUrl.Scheme != Uri.UriSchemeHttps)
            problems.Add("DisclosureUrl must be an absolute https URL.");
        return problems;
    }

    [GeneratedRegex(@"\{(?<name>sponsor|contact)\}")]
    private static partial Regex PlaceholderPattern();
}
