using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// Fingerprints a tool call for the repeat guard. The caller passes arguments in one canonical form (the same text
/// the dispatcher hashes for its log), so identical calls get identical fingerprints.
/// </summary>
public static class ToolCallFingerprint
{
    /// <summary>Lowercase hex SHA-256 of the tool name and canonical arguments.</summary>
    public static string Of(string toolName, string canonicalArguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(canonicalArguments);

        var bytes = Encoding.UTF8.GetBytes(toolName + '\0' + canonicalArguments);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
