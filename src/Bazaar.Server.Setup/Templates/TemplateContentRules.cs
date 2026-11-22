using System.Text.RegularExpressions;

namespace Bazaar.Server.Setup;

/// <summary>
/// What a template never contains (doc 45 section 1.8). Two checks run on <c>template.json</c>: a deny-list of key names, grouped by
/// category, and a scan of every string (values and key names) for a secret reference. Messages built from these rules name the
/// path and the category, never the value that was found.
/// </summary>
/// <remarks>
/// Key names are compared after normalization: lower-case, with every character that is not a letter or digit removed. So
/// <c>Api_Key</c>, <c>api-key</c> and <c>apiKey</c> all match the same rule. Exact names match the whole normalized key. Suffix
/// rules match the end of it, so <c>appIdRef</c> and <c>keyRef</c> are caught. Names are not split on dots, so a registry key such
/// as <c>host.status.bind</c> is compared as one name and is not caught by a segment rule.
/// </remarks>
public static partial class TemplateContentRules
{
    /// <summary>A secret or a reference to one.</summary>
    public const string SecretCategory = "a secret or a reference to one";

    /// <summary>GC messages (doc 01). Templates carry GC types, never messages.</summary>
    public const string GcMessageCategory = "GC messages";

    /// <summary>Soul instances, memories and agent history (doc 07, doc 08).</summary>
    public const string SoulCategory = "Soul instances, memories or agent history";

    /// <summary>Personal data about any person.</summary>
    public const string PersonalDataCategory = "personal data";

    /// <summary>The owner's identity (doc 32).</summary>
    public const string OwnerCategory = "an owner identity";

    /// <summary>Provider keys and GitHub or other provider accounts (doc 14, doc 21).</summary>
    public const string AccountCategory = "provider or GitHub account keys";

    private static readonly Dictionary<string, string> _deniedNames = new(StringComparer.Ordinal)
    {
        ["token"] = SecretCategory,
        ["accesstoken"] = SecretCategory,
        ["refreshtoken"] = SecretCategory,
        ["bearertoken"] = SecretCategory,

        ["message"] = GcMessageCategory,
        ["messages"] = GcMessageCategory,
        ["gcmessage"] = GcMessageCategory,
        ["gcmessages"] = GcMessageCategory,
        ["gcmessageversion"] = GcMessageCategory,
        ["gcmessageversions"] = GcMessageCategory,
        ["transcript"] = GcMessageCategory,
        ["transcripts"] = GcMessageCategory,

        ["soul"] = SoulCategory,
        ["souls"] = SoulCategory,
        ["soulinstance"] = SoulCategory,
        ["soulinstances"] = SoulCategory,
        ["memory"] = SoulCategory,
        ["memories"] = SoulCategory,
        ["agenthistory"] = SoulCategory,
        ["agentmemory"] = SoulCategory,
        ["agentmemories"] = SoulCategory,
        ["soulmemory"] = SoulCategory,
        ["soulmemories"] = SoulCategory,

        ["personaldata"] = PersonalDataCategory,
        ["personal"] = PersonalDataCategory,
        ["email"] = PersonalDataCategory,
        ["emailaddress"] = PersonalDataCategory,
        ["phone"] = PersonalDataCategory,
        ["phonenumber"] = PersonalDataCategory,
        ["postaladdress"] = PersonalDataCategory,
        ["dateofbirth"] = PersonalDataCategory,
        ["birthdate"] = PersonalDataCategory,
        ["nationalid"] = PersonalDataCategory,
        ["fullname"] = PersonalDataCategory,
        ["firstname"] = PersonalDataCategory,
        ["lastname"] = PersonalDataCategory,
        ["ipaddress"] = PersonalDataCategory,

        ["owner"] = OwnerCategory,
        ["ownerid"] = OwnerCategory,
        ["owneridentity"] = OwnerCategory,
        ["ownername"] = OwnerCategory,
        ["ownerdisplayname"] = OwnerCategory,
        ["owneremail"] = OwnerCategory,
        ["ownerusername"] = OwnerCategory,

        ["provider"] = AccountCategory,
        ["providers"] = AccountCategory,
        ["providerkey"] = AccountCategory,
        ["providerkeys"] = AccountCategory,
        ["account"] = AccountCategory,
        ["accounts"] = AccountCategory,
        ["githubaccount"] = AccountCategory,
        ["githubaccounts"] = AccountCategory,
        ["githubapp"] = AccountCategory,
        ["githubappid"] = AccountCategory,
        ["githubtoken"] = AccountCategory,
        ["githubinstallation"] = AccountCategory,
    };

    /// <summary>Suffix rules, checked after the exact names. The suffix must end the normalized key.</summary>
    private static readonly (string Suffix, string Category)[] _deniedSuffixes =
    [
        ("secret", SecretCategory),
        ("secrets", SecretCategory),
        ("password", SecretCategory),
        ("passwd", SecretCategory),
        ("passphrase", SecretCategory),
        ("apikey", SecretCategory),
        ("privatekey", SecretCategory),
        ("credential", SecretCategory),
        ("credentials", SecretCategory),
        ("keyref", SecretCategory),
        ("kekref", SecretCategory),
        ("appidref", SecretCategory),
    ];

    /// <summary>
    /// Secret references (doc 45 section 1.6). A <c>env:</c>, <c>file:</c>, <c>dpapi:</c> or <c>systemd-creds:</c> prefix counts only at the
    /// start of a word, so prose such as <c>profile:</c> does not match. <c>secret://</c> counts anywhere.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:env|file|dpapi|systemd-creds):|secret://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    /// <summary>
    /// The category a key name falls under, or null when the name is allowed. The result is a category label, never the name's
    /// value, so an error built from it does not repeat anything the author wrote.
    /// </summary>
    public static string? KeyCategory(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = Normalize(name);
        if (_deniedNames.TryGetValue(normalized, out var category))
        {
            return category;
        }

        foreach (var (suffix, suffixCategory) in _deniedSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                return suffixCategory;
            }
        }

        return null;
    }

    /// <summary>True when the text contains a secret reference: <c>env:</c>, <c>file:</c>, <c>dpapi:</c>, <c>systemd-creds:</c> or <c>secret://</c>.</summary>
    public static bool HasSecretReference(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ReferenceRegex().IsMatch(text);
    }

    private static string Normalize(string name)
    {
        var letters = name.Where(char.IsLetterOrDigit).ToArray();
        return new string(letters).ToLowerInvariant();
    }
}
