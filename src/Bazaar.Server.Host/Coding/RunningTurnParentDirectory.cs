using Bazaar.Server.Agents.Turns;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds <see cref="IRuntimeSubagentParentDirectory"/> to the running-turn registry (doc 06, "Running-turn registry"). A turn the
/// registry does not hold is not running, so the lookup returns null and the spawn is refused; nothing is defaulted.
/// </summary>
public sealed class RunningTurnParentDirectory : IRuntimeSubagentParentDirectory
{
    private readonly IRunningTurnRegistry _registry;

    /// <summary>Creates the directory over the registry.</summary>
    public RunningTurnParentDirectory(IRunningTurnRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public ValueTask<RuntimeSubagentParent?> FindAsync(Guid parentTurnId, CancellationToken cancellationToken)
    {
        var info = _registry.TryGet(parentTurnId);
        return ValueTask.FromResult<RuntimeSubagentParent?>(info is null
            ? null
            : new RuntimeSubagentParent(
                info.AgentId,
                info.SessionId,
                info.LeaseHolder,
                info.ToolAllowlist,
                info.StepCap,
                info.TriggerGcId,
                info.TriggerGcSeq));
    }
}

/// <summary>Registration for <see cref="RunningTurnParentDirectory"/>.</summary>
public static class RunningTurnParentDirectoryExtensions
{
    /// <summary>
    /// Registers the in-process running-turn registry and binds <see cref="IRuntimeSubagentParentDirectory"/> to it. A directory
    /// the host registered earlier is kept.
    /// </summary>
    public static IServiceCollection AddRunningTurnParentDirectory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRunningTurnRegistry();
        services.TryAddSingleton<IRuntimeSubagentParentDirectory>(sp =>
            new RunningTurnParentDirectory(sp.GetRequiredService<IRunningTurnRegistry>()));
        return services;
    }
}
