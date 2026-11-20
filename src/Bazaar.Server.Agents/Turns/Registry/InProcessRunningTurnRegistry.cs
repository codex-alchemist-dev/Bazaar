using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Agents.Turns;

/// <summary>The in-process running-turn registry (doc 06, "Running-turn registry"). Nothing is persisted: a restart starts empty.</summary>
public sealed class InProcessRunningTurnRegistry : IRunningTurnRegistry, IRunningTurnRegistrar
{
    private readonly ConcurrentDictionary<Guid, RunningTurnInfo> _turns = new();

    /// <inheritdoc />
    public RunningTurnInfo? TryGet(Guid turnId) => _turns.TryGetValue(turnId, out var info) ? info : null;

    /// <inheritdoc />
    public IDisposable Register(RunningTurnInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(info.ToolAllowlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.LeaseHolder);
        if (info.TurnId == Guid.Empty)
            throw new ArgumentException("A running turn needs its turn id.", nameof(info));

        // The entry owns its own read-only copy of the allowlist, so a caller cannot change what the turn holds afterwards.
        var stored = info with { ToolAllowlist = Array.AsReadOnly(info.ToolAllowlist.ToArray()) };
        if (!_turns.TryAdd(stored.TurnId, stored))
            throw new InvalidOperationException($"Turn '{stored.TurnId}' is already registered as running.");
        return new Registration(this, stored);
    }

    private sealed class Registration(InProcessRunningTurnRegistry owner, RunningTurnInfo entry) : IDisposable
    {
        // Removes only this registration's own entry, so a late dispose cannot drop a later turn that reused the id.
        public void Dispose() => owner._turns.TryRemove(new KeyValuePair<Guid, RunningTurnInfo>(entry.TurnId, entry));
    }
}

/// <summary>Service registration for the running-turn registry.</summary>
public static class RunningTurnRegistryServiceCollectionExtensions
{
    /// <summary>Registers one in-process registry under its read and write interfaces.</summary>
    public static IServiceCollection AddRunningTurnRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InProcessRunningTurnRegistry>();
        services.TryAddSingleton<IRunningTurnRegistry>(sp => sp.GetRequiredService<InProcessRunningTurnRegistry>());
        services.TryAddSingleton<IRunningTurnRegistrar>(sp => sp.GetRequiredService<InProcessRunningTurnRegistry>());
        return services;
    }
}
