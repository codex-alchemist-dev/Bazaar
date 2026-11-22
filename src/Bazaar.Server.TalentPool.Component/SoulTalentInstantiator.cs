using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Souls;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>Creates the agent row. Narrow port so the instantiation steps can be tested without the agent schema.</summary>
public interface IAgentCreator
{
    /// <summary>Creates an agent in the Created state and returns its id.</summary>
    Task<Guid> CreateAsync(string name, CancellationToken ct);
}

/// <summary>Appends a Soul version. Narrow port over the Soul version chain.</summary>
public interface ISoulVersionAppender
{
    /// <summary>Appends one version.</summary>
    Task AppendAsync(SoulVersionAppend append, CancellationToken ct);
}

/// <summary>Writes an agent's deny-by-default model policy. Narrow port over the gateway policy table.</summary>
public interface IAgentModelPolicyWriter
{
    /// <summary>Saves exactly these provider-qualified models as the agent's whitelist.</summary>
    Task SaveAsync(Guid agentId, IReadOnlyList<string> models, string changedBy, CancellationToken ct);
}

internal sealed class AgentRepositoryCreator(AgentRepository agents) : IAgentCreator
{
    public Task<Guid> CreateAsync(string name, CancellationToken ct) => agents.CreateAsync(name, soulVersionId: null, ct);
}

internal sealed class SoulVersionAppender(SoulVersioningService souls) : ISoulVersionAppender
{
    public async Task AppendAsync(SoulVersionAppend append, CancellationToken ct) => await souls.AppendAsync(append, ct).ConfigureAwait(false);
}

internal sealed class GatewayModelPolicyWriter(AgentModelPolicyRepository policy) : IAgentModelPolicyWriter
{
    public async Task SaveAsync(Guid agentId, IReadOnlyList<string> models, string changedBy, CancellationToken ct) =>
        await policy.SaveAsync(agentId.ToString(), models, defaultRoute: null, changedBy, ct).ConfigureAwait(false);
}

/// <summary>
/// Creates the agent for a released template through doc 07 Creation, as far as the Soul and runtime modules reach today:
/// the agent row, Soul version 1 (charter hash and preferences), the agent-to-Soul binding, and a deny-by-default model policy
/// that lists exactly the manifest's models. GitHub App, email and VM disks are created by their own modules on the
/// agent-created lifecycle step. Tool grants are recorded as preferences only; they take effect through the Tier 2 approval flow.
/// </summary>
public sealed class SoulTalentInstantiator(
    IAgentCreator agents,
    ISoulVersionAppender souls,
    ISoulBindingAdmin bindings,
    IAgentModelPolicyWriter modelPolicy) : ITalentInstantiator
{
    /// <inheritdoc />
    public async Task<string> CreateAgentAsync(TalentTemplate candidate, string actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var agentId = await agents.CreateAsync(candidate.Manifest.Name, ct).ConfigureAwait(false);
        var soulId = Guid.NewGuid();
        var charterSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Manifest.Prompt)));
        var preferences = JsonSerializer.Serialize(
            new HirePreferences(candidate.HireKey, candidate.ContentSha, candidate.Manifest.ToolAllowlist),
            TalentJson.Default.HirePreferences);

        await souls.AppendAsync(
            new SoulVersionAppend(soulId, 0, charterSha, preferences, actorId, $"Hired from Talent Pool template {candidate.HireKey}", candidate.HireKey),
            ct).ConfigureAwait(false);
        await bindings.BindAsync(agentId, soulId, $"Talent Pool hire {candidate.HireKey}", ct).ConfigureAwait(false);
        await modelPolicy.SaveAsync(agentId, candidate.Manifest.ModelWhitelist, actorId, ct).ConfigureAwait(false);
        return agentId.ToString();
    }
}
