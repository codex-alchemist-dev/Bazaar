using System.Text.Json.Serialization;
using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.SealedPassport;

/// <summary>Scope names and logical paths of the sealed Passport (doc 61 WP4).</summary>
internal static class PassportScopes
{
    /// <summary>Profile id to agent id. Holds no secret; lets the browser port find a profile by id alone.</summary>
    public const string Index = "passport-index";

    /// <summary>The jar's logical path inside its cookie scope.</summary>
    public const string JarPath = "jar.json";

    public static string Agent(string agentId) => "passport:" + agentId;

    public static string Cookies(Guid profileId) => "cookies:" + profileId.ToString("D");

    public static string Directory(Guid profileId) => "browserdir:" + profileId.ToString("D");

    public static string IndexPath(Guid profileId) => "profiles/" + profileId.ToString("D");

    public static string ProfilePath(Guid profileId) => "profiles/" + profileId.ToString("D") + ".json";

    /// <summary>Agent ids become part of a scope name: letters, digits, '.', '_' and '-' only, at most 128 characters.</summary>
    public static void EnsureSafeAgentId(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (agentId.Length > 128 || !agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || agentId is "." or "..")
        {
            throw new ArgumentException("Agent id must be 1-128 characters of letters, digits, '.', '_' or '-'.", nameof(agentId));
        }
    }
}

/// <summary>Site rules for the allowlist (same semantics as doc 14 Identity: a cookie may narrow an allowlist entry, never widen it).</summary>
internal static class PassportDomains
{
    public static string Normalize(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var d = domain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0 || d.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
        {
            throw new ArgumentException("A site must be a plain host name.", nameof(domain));
        }

        return d;
    }

    /// <summary>True when <paramref name="cookieDomain"/> is <paramref name="allowed"/> or a subdomain of it.</summary>
    public static bool IsWithin(string cookieDomain, string allowed)
    {
        string d;
        try
        {
            d = Normalize(cookieDomain);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return d == allowed || d.EndsWith("." + allowed, StringComparison.Ordinal);
    }
}

/// <summary>A profile's sealed metadata.</summary>
internal sealed record ProfileRecord(Guid ProfileId, string AgentId, BrowserProfileClass Class, string[] ServiceAllowlist)
{
    public BrowserProfileRef ToRef() => new(ProfileId, AgentId, Class, ServiceAllowlist);
}

/// <summary>Source-generated JSON, so the sealed payload format is fixed at build time.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProfileRecord))]
[JsonSerializable(typeof(BrowserCookieEntry[]))]
internal sealed partial class PassportJsonContext : JsonSerializerContext;
