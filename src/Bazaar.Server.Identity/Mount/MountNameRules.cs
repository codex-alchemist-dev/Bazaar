namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// Name checks for the mounted folder. Agent ids, service names and file names become path segments, so they are kept
/// to a small safe alphabet. File names must also not look like a forbidden class (keys, DEKs, Passport, audit).
/// </summary>
internal static class MountNameRules
{
    /// <summary>Fragments that a mounted file name must never contain (case-insensitive).</summary>
    private static readonly string[] _forbiddenNameFragments =
    [
        "key", "dek", "passport", "audit", "secret", "token", "totp", "recovery", "passkey", "ssh", "oauth", "password", "seed",
    ];

    /// <summary>Checks an agent id used as a directory name.</summary>
    public static void EnsureSafeAgentId(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (agentId.Length > 64 || !char.IsAsciiLetterOrDigit(agentId[0]))
        {
            throw new ArgumentException("Agent id must be 1 to 64 characters and start with a letter or digit.", nameof(agentId));
        }

        foreach (var c in agentId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new ArgumentException("Agent id may contain only ASCII letters, digits, '-' and '_'.", nameof(agentId));
            }
        }
    }

    /// <summary>Checks a service name used in canary entries (lower-case letters, digits and '-').</summary>
    public static void EnsureSafeServiceName(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (serviceName.Length > 63 || !IsLowerAsciiAlnum(serviceName[0]))
        {
            throw new ArgumentException("Service name must be 1 to 63 characters and start with a lower-case letter or digit.", nameof(serviceName));
        }

        foreach (var c in serviceName)
        {
            if (!IsLowerAsciiAlnum(c) && c != '-')
            {
                throw new ArgumentException("Service name may contain only lower-case ASCII letters, digits and '-'.", nameof(serviceName));
            }
        }
    }

    /// <summary>Checks a single file name inside an agent's folder: no separators, no traversal, no forbidden class names.</summary>
    public static void EnsureSafeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > 128 || fileName[0] == '.')
        {
            throw new ArgumentException("File name must be 1 to 128 characters and must not start with '.'.", nameof(fileName));
        }

        foreach (var c in fileName)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
            {
                throw new ArgumentException("File name may contain only ASCII letters, digits, '-', '_' and '.'.", nameof(fileName));
            }
        }

        var lower = fileName.ToLowerInvariant();
        foreach (var fragment in _forbiddenNameFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
            {
                throw new ArgumentException("File name matches a forbidden credential class and may not be mounted.", nameof(fileName));
            }
        }
    }

    private static bool IsLowerAsciiAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
