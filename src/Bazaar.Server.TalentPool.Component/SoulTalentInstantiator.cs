using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Souls;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>
/// Creates the agent for a released template through doc 07 Creation, as far as the Soul and runtime modules reach today:
/// the agent row, Soul version 1 (charter hash and preferences), the agent-to-Soul binding, and a deny-by-default model policy
/// that lists exactly the manifest's models. GitHub App, email and VM disks are created by their own modules on the
/// agent-created lifecycle step. Tool grants are recorded as preferences only; they take effect through the Tier 2 approval flow.
/// </summary>
public sealed class SoulTalentInstantiator(
    AgentRepository agents,
    SoulVersioningService souls,
    ISoulBindingAdmin bindings,
    AgentModelPolicyRepository modelPolicy) : ITalentInstantiator
{
    /// <inheritdoc />
    public async Task<string> CreateAgentAsync(TalentTemplate candidate, string actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var agentId = await agents.CreateAsync(candidate.Manifest.Name, soulVersionId: null, ct).ConfigureAwait(false);
        var soulId = Guid.NewGuid();
        var charterSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Manifest.Prompt)));
        var preferences = JsonSerializer.Serialize(
            new HirePreferences(candidate.HireKey, candidate.ContentSha, candidate.Manifest.ToolAllowlist),
            TalentJson.Default.HirePreferences);

        await souls.AppendAsync(
            new SoulVersionAppend(soulId, 0, charterSha, preferences, actorId, $"Hired from Talent Pool template {candidate.HireKey}", candidate.HireKey),
            ct).ConfigureAwait(false);
        await bindings.BindAsync(agentId, soulId, $"Talent Pool hire {candidate.HireKey}", ct).ConfigureAwait(false);
        await modelPolicy.SaveAsync(agentId.ToString(), candidate.Manifest.ModelWhitelist, defaultRoute: null, changedBy: actorId, ct).ConfigureAwait(false);
        return agentId.ToString();
    }
}
