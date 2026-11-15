using System.Text.RegularExpressions;

namespace Bazaar.Server.Vault.Search;

/// <summary>
/// The form of a private document's <c>owner</c> front-matter value (doc 18, "Access"). It is the same string
/// <see cref="VaultCaller.AgentOwner"/> carries: <c>agent:</c> followed by the agent id in lowercase "D" GUID form. Access
/// is an exact string match, so any other spelling would make a private document invisible to its own agent.
/// </summary>
public static partial class VaultOwner
{
    public static string ForAgent(Guid agentId) => "agent:" + agentId.ToString("D");

    /// <summary>True when <paramref name="owner"/> is null (a shared document) or has the agent form.</summary>
    public static bool IsWellFormed(string? owner) => owner is null || AgentPattern().IsMatch(owner);

    [GeneratedRegex("^agent:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentPattern();
}
