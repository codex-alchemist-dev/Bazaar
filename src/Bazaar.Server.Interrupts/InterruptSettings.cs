using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Interrupts;

/// <summary>Which agents a system interrupt frame targets (doc 24 section 3, <c>interrupt.system_targets</c>). Wire form is snake_case.</summary>
public enum InterruptSystemTargets
{
    /// <summary>No system frame is pushed. The shipped default.</summary>
    None,

    /// <summary>The members of the project the event is about. No production membership source exists yet, so this pushes nobody.</summary>
    ProjectMembers,

    /// <summary>Every agent that is not retired.</summary>
    AllAgents,
}

/// <summary>The Interrupt System's operator settings (doc 24 section 3 settings table).</summary>
public static class InterruptSettings
{
    /// <summary>Which agents system frames (build broken, security) go to. Off by default: a system frame suspends an agent's work.</summary>
    public static readonly SettingKey<InterruptSystemTargets> SystemTargets = new(
        "interrupt.system_targets", InterruptSystemTargets.None, SettingScope.Install, SettingRole.Operator,
        "Which agents get a system interrupt when a build breaks or a session is tainted: none, project_members or all_agents.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [SystemTargets];
}

/// <summary>The Interrupts module's contribution to the settings registry. The host composes it with the built-in keys.</summary>
public sealed class InterruptSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => InterruptSettings.All;
}

/// <summary>
/// Reads the current <c>interrupt.system_targets</c> value. The settings store lives in Bazaar.Server, which this module does not
/// reference, so the host implements it over <c>ISettingStore</c> (the Wire discovery precedent).
/// </summary>
public interface IInterruptSystemTargetsReader
{
    Task<InterruptSystemTargets> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The GC where a system frame for an agent is announced: the GC in the agent's presence row. Null when the agent is not working
/// in one, and then no frame is pushed.
/// </summary>
public interface IInterruptGcResolver
{
    Task<Guid?> ResolveAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the working GC from the presence store (doc 23, <c>agent_activity.gc_id</c>).</summary>
public sealed class PresenceInterruptGcResolver(IPresenceStore presence) : IInterruptGcResolver
{
    public async Task<Guid?> ResolveAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        (await presence.GetAsync(agentId, cancellationToken).ConfigureAwait(false))?.GcId;
}

/// <summary>Used when no presence store is registered: no agent has a working GC, so no system frame is pushed.</summary>
public sealed class NoInterruptGcResolver : IInterruptGcResolver
{
    public Task<Guid?> ResolveAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
}
