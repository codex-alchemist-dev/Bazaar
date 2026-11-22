using System.Collections.Concurrent;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingRunner;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Coding;

/// <summary>Finds the live task worktree a worktree key names. A key the directory does not hold is no worktree.</summary>
public interface ICodingWorktreeDirectory
{
    /// <summary>The worktree held under <paramref name="worktreeKey"/>, or null.</summary>
    CodingWorktree? Find(string worktreeKey);
}

/// <summary>
/// In-memory <see cref="ICodingWorktreeDirectory"/>. Whoever opens a task worktree registers it under a key the model cannot
/// choose; disposing the registration removes it. Nothing is persisted.
/// </summary>
public sealed class CodingWorktreeRegistry : ICodingWorktreeDirectory
{
    private readonly ConcurrentDictionary<string, CodingWorktree> _worktrees = new(StringComparer.Ordinal);

    /// <summary>Adds the worktree under the key. Disposing the result removes it.</summary>
    /// <exception cref="InvalidOperationException">The key is already in use.</exception>
    public IDisposable Register(string worktreeKey, CodingWorktree worktree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeKey);
        ArgumentNullException.ThrowIfNull(worktree);
        if (!_worktrees.TryAdd(worktreeKey, worktree))
        {
            throw new InvalidOperationException("A worktree is already registered under that key.");
        }

        return new Registration(_worktrees, worktreeKey, worktree);
    }

    /// <inheritdoc />
    public CodingWorktree? Find(string worktreeKey) => _worktrees.TryGetValue(worktreeKey, out var worktree) ? worktree : null;

    private sealed class Registration(ConcurrentDictionary<string, CodingWorktree> map, string key, CodingWorktree worktree) : IDisposable
    {
        public void Dispose() => map.TryRemove(new KeyValuePair<string, CodingWorktree>(key, worktree));
    }
}

/// <summary>What the host knows about the model one agent runs as.</summary>
/// <param name="ModelId">The agent's own model id.</param>
/// <param name="CreditName">The name credited as <c>Co-Authored-By</c>.</param>
/// <param name="CreditEmail">The credit email.</param>
/// <param name="ModelWhitelist">The models the agent may use (doc 05), which bounds its subagents.</param>
public sealed record CodingAgentModel(string ModelId, string CreditName, string CreditEmail, IReadOnlySet<string> ModelWhitelist);

/// <summary>The model facts per agent. Null means the agent is unknown, which refuses the call.</summary>
public interface ICodingAgentModels
{
    /// <summary>Returns the agent's model facts, or null.</summary>
    CodingAgentModel? Find(string agentId);
}

/// <summary>Model facts read from configuration (<c>Coding:Harness:Models:&lt;agentId&gt;</c>), until a gateway-backed source exists.</summary>
public sealed class ConfiguredCodingAgentModels : ICodingAgentModels
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Coding:Harness:Models";

    private readonly Dictionary<string, CodingAgentModel> _models;

    /// <summary>Creates the source over a copy of the map.</summary>
    public ConfiguredCodingAgentModels(IReadOnlyDictionary<string, CodingAgentModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = new Dictionary<string, CodingAgentModel>(models, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the section. An entry needs <c>ModelId</c>, <c>Name</c> and <c>Email</c>, the name and email free of control characters
    /// and angle brackets, and the email a plain address; a bad entry throws, naming the key, so a host start stops.
    /// </summary>
    public static ConfiguredCodingAgentModels FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var map = new Dictionary<string, CodingAgentModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection(SectionName).GetChildren())
        {
            var key = $"{SectionName}:{entry.Key}";
            var modelId = entry["ModelId"];
            var name = entry["Name"]?.Trim();
            var email = entry["Email"]?.Trim();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new InvalidOperationException($"{key}:ModelId is required.");
            }

            if (name is null || !GitIdentityText.IsSafeName(name))
            {
                throw new InvalidOperationException($"{key}:Name is empty or has control characters or angle brackets.");
            }

            if (email is null || !GitIdentityText.IsSafeEmail(email))
            {
                throw new InvalidOperationException($"{key}:Email is not a plain address.");
            }

            var whitelist = entry.GetSection("Whitelist").GetChildren()
                .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
                .ToHashSet(StringComparer.Ordinal)!;
            map[entry.Key] = new CodingAgentModel(modelId.Trim(), name, email, whitelist!);
        }

        return new ConfiguredCodingAgentModels(map);
    }

    /// <inheritdoc />
    public CodingAgentModel? Find(string agentId) => _models.TryGetValue(agentId, out var model) ? model : null;
}

/// <summary>
/// Binds <see cref="ICodingToolHostContext"/> (doc 17, host sources for worktree files and tool context). The calling agent and
/// session come from the call and must match exactly one running turn in <see cref="IRunningTurnIndex"/>; with no match, a
/// non-GUID id or more than one match (a parent and its subagent share a session and the call does not say which one is calling)
/// there is no context, so worktree lookups, model facts and parent limits are all refused. Nothing is defaulted.
/// </summary>
public sealed class RunningTurnCodingToolHostContext : ICodingToolHostContext
{
    private readonly IRunningTurnIndex _turns;
    private readonly ICodingWorktreeDirectory _worktrees;
    private readonly ICodingAgentModels _models;

    /// <summary>Creates the context over the running-turn index, the worktree directory and the model facts.</summary>
    public RunningTurnCodingToolHostContext(IRunningTurnIndex turns, ICodingWorktreeDirectory worktrees, ICodingAgentModels models)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(models);
        _turns = turns;
        _worktrees = worktrees;
        _models = models;
    }

    /// <inheritdoc />
    public CodingWorktree? FindWorktree(string worktreeKey)
    {
        if (string.IsNullOrWhiteSpace(worktreeKey))
        {
            return null;
        }

        var worktree = _worktrees.Find(worktreeKey);
        return worktree is { IsActive: true } ? worktree : null;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The agent has no running turn or no configured model.</exception>
    public ModelIdentity CommitModelFor(string agentId)
    {
        var model = ModelFor(agentId);
        return new ModelIdentity(model.CreditName, model.CreditEmail);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The agent has no running turn or no configured model.</exception>
    public string ModelIdFor(string agentId) => ModelFor(agentId).ModelId;

    /// <inheritdoc />
    public ParentContext? ParentFor(CodingToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var turn = CallingTurn(toolCall.AgentId, toolCall.SessionId);
        var model = turn is null ? null : _models.Find(toolCall.AgentId);
        if (turn is null || model is null)
        {
            return null;
        }

        // A subagent inherits its parent's session id, so the session is the chain root at every depth.
        return new ParentContext(toolCall.SessionId, toolCall.SessionId, new HashSet<string>(turn.ToolAllowlist, StringComparer.Ordinal), model.ModelWhitelist, turn.Depth);
    }

    private CodingAgentModel ModelFor(string agentId)
    {
        if (!Guid.TryParse(agentId, out var id) || _turns.ForAgent(id).Count == 0)
        {
            throw new InvalidOperationException("The agent has no running turn, so no model facts are returned.");
        }

        return _models.Find(agentId)
            ?? throw new InvalidOperationException("No model is configured for this agent, so none is credited or used.");
    }

    private RunningTurnInfo? CallingTurn(string agentId, string sessionId)
    {
        if (!Guid.TryParse(agentId, out var agent) || !Guid.TryParse(sessionId, out var session))
        {
            return null;
        }

        var matches = _turns.ForAgent(agent).Where(t => t.SessionId == session).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

/// <summary>Registration for the worktree-files source and the tool host context.</summary>
public static class CodingToolHostContextExtensions
{
    /// <summary>
    /// Registers <see cref="IWorktreeFilesSource"/>, <see cref="ICodingToolHostContext"/>, <see cref="ICodingWorktreeDirectory"/>
    /// (the shared <see cref="CodingWorktreeRegistry"/>) and <see cref="ICodingAgentModels"/> (from configuration). A host
    /// registration made first wins; dependencies resolve lazily.
    /// </summary>
    public static IServiceCollection AddBazaarCodingToolHostContext(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var models = ConfiguredCodingAgentModels.FromConfiguration(configuration);
        services.TryAddSingleton<CodingWorktreeRegistry>();
        services.TryAddSingleton<ICodingWorktreeDirectory>(sp => sp.GetRequiredService<CodingWorktreeRegistry>());
        services.TryAddSingleton<ICodingAgentModels>(models);
        services.TryAddSingleton<IWorktreeFilesSource>(sp => new WorktreeFilesSource(sp.GetRequiredService<IWorkShareLayout>()));
        services.TryAddSingleton<ICodingToolHostContext>(sp => new RunningTurnCodingToolHostContext(
            sp.GetRequiredService<IRunningTurnIndex>(),
            sp.GetRequiredService<ICodingWorktreeDirectory>(),
            sp.GetRequiredService<ICodingAgentModels>()));
        return services;
    }
}
