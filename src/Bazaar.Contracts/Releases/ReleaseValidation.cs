using System.Text.RegularExpressions;

namespace Bazaar.Contracts.Releases;

/// <summary>Field checks shared by the release records. Every failure is an <see cref="ArgumentException"/> naming the parameter.</summary>
internal static partial class ReleaseValidation
{
    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(-(?<pre>[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*))?(\\+[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();

    [GeneratedRegex("^(0|[1-9]\\d{0,8})\\.(0|[1-9]\\d{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaPattern();

    [GeneratedRegex("^(alpha|beta|rc|dev)\\.(0|[1-9]\\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PreReleasePattern();

    public static string Known(string? value, IReadOnlyList<string> allowed, string name)
    {
        if (value is null || !allowed.Contains(value))
        {
            throw new ArgumentException($"'{value}' is not one of: {string.Join(", ", allowed)}.", name);
        }

        return value;
    }

    public static string LowerHex(string? value, int length, string name)
    {
        if (value is null || value.Length != length)
        {
            throw new ArgumentException($"Expected {length} lowercase hex characters.", name);
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                throw new ArgumentException($"Expected {length} lowercase hex characters.", name);
            }
        }

        return value;
    }

    public static string SemVer(string? value, string name)
    {
        if (value is null || value.Length > 128 || !SemVerPattern().IsMatch(value))
        {
            throw new ArgumentException("Expected a SemVer version such as 0.1.0 or 0.2.0-beta.1.", name);
        }

        return value;
    }

    /// <summary>The channel is decided by the pre-release suffix (docs/VERSIONING.md section 4).</summary>
    public static void ChannelMatchesVersion(string channel, string version, string name)
    {
        var match = SemVerPattern().Match(version);
        string pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : string.Empty;
        string? tag = null;
        if (pre.Length > 0)
        {
            var parsed = PreReleasePattern().Match(pre);
            if (parsed.Success) tag = parsed.Groups[1].Value;
        }

        bool ok = channel switch
        {
            ReleaseChannels.Stable or ReleaseChannels.Ltsc => pre.Length == 0,
            ReleaseChannels.Beta => tag is "beta" or "rc",
            ReleaseChannels.Alpha => tag == "alpha",
            ReleaseChannels.LatestCommit => tag == "dev",
            _ => false,
        };
        if (!ok)
        {
            throw new ArgumentException($"Version '{version}' does not belong to channel '{channel}'.", name);
        }
    }

    /// <summary>Standard padded base64 that decodes to at least one byte, at most <see cref="ReleaseArtifact.MaxSignatureLength"/> characters.</summary>
    public static string Base64(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ReleaseArtifact.MaxSignatureLength || value.Length % 4 != 0)
        {
            throw new ArgumentException("An inline signature of padded base64 (1 to 2048 characters) is required.", name);
        }

        Span<byte> buffer = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out int written) || written == 0)
        {
            throw new ArgumentException("The inline signature is not valid base64.", name);
        }

        return value;
    }

    public static Version Protocol(string? value, string name)
    {
        if (value is null || !ProtocolPattern().IsMatch(value))
        {
            throw new ArgumentException("Expected a protocol version as major.minor, such as 1.0.", name);
        }

        return Version.Parse(value);
    }

    public static string SchemaVersion(string? value, string name)
    {
        if (value is null || !SchemaPattern().IsMatch(value))
        {
            throw new ArgumentException("Expected 1 to 32 characters of letters, digits, '.', '_' or '-'.", name);
        }

        return value;
    }

    /// <summary>
    /// Absolute https URLs (host required, no credentials, query or fragment) or URLs relative to the manifest's own
    /// address (no scheme, no scheme-relative form, no backslash, no <c>..</c> segment, no query or fragment).
    /// </summary>
    public static string Url(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ReleaseArtifact.MaxUrlLength)
        {
            throw new ArgumentException($"A URL of 1 to {ReleaseArtifact.MaxUrlLength} characters is required.", name);
        }

        foreach (char c in value)
        {
            if (c <= ' ' || c == '\\' || c == '?' || c == '#' || c == 0x7f)
            {
                throw new ArgumentException("URL contains a forbidden character (space, control, backslash, query or fragment).", name);
            }
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.Host.Length == 0
                || uri.UserInfo.Length != 0
                || value.Contains('@', StringComparison.Ordinal))
            {
                throw new ArgumentException("An absolute URL must be https with a host and no credentials.", name);
            }

            return value;
        }

        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A relative URL must not carry a scheme or authority.", name);
        }

        foreach (string segment in value.Split('/'))
        {
            if (segment == "..")
            {
                throw new ArgumentException("A relative URL must not contain '..'.", name);
            }
        }

        return value;
    }
}
