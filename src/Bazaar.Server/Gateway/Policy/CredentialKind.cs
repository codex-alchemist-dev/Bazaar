namespace Bazaar.Server.Gateway.Policy;

/// <summary>
/// The only credential kinds a provider may use. Subscription logins (Claude Pro/Max, Claude Code tiers, Copilot,
/// ChatGPT, Gemini CLI) and OAuth sessions are not kinds, so they cannot be configured, stored or accepted.
/// </summary>
public enum CredentialKind
{
    ApiKey,
    Bearer,
    None,
}

public static class CredentialKinds
{
    public const string SecretRefPrefix = "secret://bazaar/providers/";

    public static bool TryParse(string? text, out CredentialKind kind)
    {
        switch (text)
        {
            case "api_key":
                kind = CredentialKind.ApiKey;
                return true;
            case "bearer":
                kind = CredentialKind.Bearer;
                return true;
            case "none":
                kind = CredentialKind.None;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// True when the stored auth kind is one of the three allowed and the secret reference matches it: a key or bearer
    /// needs a <c>secret://bazaar/providers/</c> reference (never a literal key), and <c>none</c> carries none.
    /// </summary>
    public static bool IsAcceptable(string? authKind, string? secretRef)
    {
        if (!TryParse(authKind, out var kind))
        {
            return false;
        }

        return kind == CredentialKind.None
            ? string.IsNullOrEmpty(secretRef)
            : secretRef is not null && secretRef.StartsWith(SecretRefPrefix, StringComparison.Ordinal) && secretRef.Length > SecretRefPrefix.Length;
    }
}
