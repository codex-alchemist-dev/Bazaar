using Bazaar.Server.Agents.Data;
using Bazaar.Server.Health;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// The system interrupt producer (doc 24 section 3, "System producer wiring"). One in-process listener for the Health build-broken
/// seam and the Security taint-raised seam. It pushes through <see cref="IInterruptStackService"/> to the agents
/// <c>interrupt.system_targets</c> names; with <see cref="InterruptSystemTargets.None"/> (the default) it does nothing. Reasons are
/// fixed server text built from ids, never message text. A failed push (stack full, store down) is logged and skipped, so it never
/// fails the health collection or a tool call. Every dependency but the stack service is optional: a missing one means no targets.
/// </summary>
public sealed partial class InterruptSystemProducer(
    IInterruptStackService stack,
    IInterruptSystemTargetsReader? targets = null,
    IAgentDirectoryReader? agents = null,
    IInterruptGcResolver? gcs = null,
    ILogger<InterruptSystemProducer>? logger = null) : IBuildBrokenListener, ITaintRaisedListener
{
    private const int _maxRefLength = 64;
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;
    private int _projectMembersLogged;

    [LoggerMessage(Level = LogLevel.Warning, Message = "interrupt.system_targets is project_members but no project membership source exists; no system frame was pushed.")]
    private static partial void LogNoMembershipSource(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "No working GC for agent {AgentId}; the system frame was not pushed.")]
    private static partial void LogNoGc(ILogger logger, Guid agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A system interrupt push for agent {AgentId} failed.")]
    private static partial void LogPushFailed(ILogger logger, Exception exception, Guid agentId);

    public Task OnBuildBrokenAsync(BuildBroken signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var reason = $"Build broken in project {Clip(signal.ProjectId)} (run {Clip(signal.RunRef)})";
        return PushAsync(InterruptSource.SystemBuild, reason, only: null, cancellationToken);
    }

    public Task OnTaintRaisedAsync(TaintRaised raised, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raised);

        // A soft rise is routine agent-to-agent text; only external content (hard) is a security event.
        if (raised.Level < TaintState.Hard || !Guid.TryParse(raised.AgentId, out var agentId))
            return Task.CompletedTask;

        return PushAsync(InterruptSource.SystemSecurity, "Session taint raised to hard", agentId, cancellationToken);
    }

    /// <summary>Pushes to the targets, or only to <paramref name="only"/> when it is one of them.</summary>
    private async Task PushAsync(InterruptSource source, string reason, Guid? only, CancellationToken ct)
    {
        var mode = targets is null ? InterruptSystemTargets.None : await targets.GetAsync(ct).ConfigureAwait(false);
        if (mode == InterruptSystemTargets.None)
            return;

        if (mode == InterruptSystemTargets.ProjectMembers)
        {
            if (Interlocked.Exchange(ref _projectMembersLogged, 1) == 0)
                LogNoMembershipSource(_log);
            return;
        }

        if (agents is null || gcs is null)
            return;

        var active = (await agents.ListActiveAgentsAsync(ct).ConfigureAwait(false)).Select(a => a.AgentId);
        foreach (var agentId in only is { } one ? active.Where(id => id == one) : active)
        {
            try
            {
                if (await gcs.ResolveAsync(agentId, ct).ConfigureAwait(false) is not { } gcId)
                {
                    LogNoGc(_log, agentId);
                    continue;
                }

                // The author is ignored for a system source; the service seals under the company owner.
                await stack.PushAsync(agentId, Guid.Empty, source, reason, gcId, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPushFailed(_log, ex, agentId);
            }
        }
    }

    private static string Clip(string value) => value.Length <= _maxRefLength ? value : value[.._maxRefLength];
}
