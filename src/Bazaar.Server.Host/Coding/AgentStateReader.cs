using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Security.Taint;
using SessionTaint = Bazaar.Server.Security.Taint.SessionTaint;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Production <see cref="IAgentStateReader"/> (doc 17, "Host-side sources"). Quarantine comes from the quarantine store and the
/// agent freeze; taint is the highest taint among the agent's running turns' sessions. The interface is synchronous and the
/// stores are not, so each read waits at most <see cref="ReadTimeout"/>; a failed or late read fails closed (quarantined, Hard).
/// </summary>
public sealed class AgentStateReader : IAgentStateReader
{
    private readonly IQuarantineStore _quarantine;
    private readonly IAgentFreeze _freeze;
    private readonly IRunningTurnIndex _turns;
    private readonly ISessionTaintStore _taint;

    /// <summary>Creates the reader.</summary>
    /// <param name="quarantine">Quarantine records.</param>
    /// <param name="freeze">Agent freeze state.</param>
    /// <param name="turns">Running turns by agent.</param>
    /// <param name="taint">Session taint.</param>
    /// <param name="readTimeout">Longest wait for one store read. Must be positive.</param>
    public AgentStateReader(IQuarantineStore quarantine, IAgentFreeze freeze, IRunningTurnIndex turns, ISessionTaintStore taint, TimeSpan readTimeout)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        ArgumentNullException.ThrowIfNull(freeze);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(taint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);
        _quarantine = quarantine;
        _freeze = freeze;
        _turns = turns;
        _taint = taint;
        ReadTimeout = readTimeout;
    }

    /// <summary>The longest wait for one store read.</summary>
    public TimeSpan ReadTimeout { get; }

    /// <inheritdoc />
    public bool IsQuarantined(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return true;
        }

        var record = BoundedWait.Run(ct => _quarantine.GetActiveAsync(new QuarantineSubject(QuarantineSubjectKind.Agent, agentId), ct), ReadTimeout);
        if (!record.Succeeded || record.Value is not null)
        {
            return true;
        }

        var frozen = BoundedWait.Run(ct => _freeze.IsFrozenAsync(agentId, ct).AsTask(), ReadTimeout);
        return !frozen.Succeeded || frozen.Value;
    }

    /// <inheritdoc />
    public SessionTaint TaintFor(string agentId)
    {
        if (!Guid.TryParse(agentId, out var id))
        {
            return Hard();
        }

        var worst = TaintState.Clean;
        var sources = new List<TaintSource>();
        foreach (var turn in _turns.ForAgent(id))
        {
            var loaded = BoundedWait.Run(ct => _taint.LoadAsync(turn.SessionId, ct), ReadTimeout);
            if (!loaded.Succeeded || loaded.Value is null)
            {
                return Hard();
            }

            if (loaded.Value.State > worst)
            {
                worst = loaded.Value.State;
            }

            sources.AddRange(loaded.Value.Sources);
        }

        return worst == TaintState.Clean ? SessionTaint.Clean : new SessionTaint(worst, [.. sources]);
    }

    private static SessionTaint Hard() => new(TaintState.Hard, []);
}

/// <summary>Runs an async read from synchronous code with a deadline. Never throws for a store failure.</summary>
internal static class BoundedWait
{
    public readonly record struct Result<T>(bool Succeeded, T? Value);

    public static Result<T> Run<T>(Func<CancellationToken, Task<T>> read, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var task = Task.Run(() => read(cts.Token), CancellationToken.None);
            if (!task.Wait(timeout))
            {
                cts.Cancel();
                return new Result<T>(false, default);
            }

            return new Result<T>(true, task.Result);
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException or InvalidOperationException)
        {
            return new Result<T>(false, default);
        }
    }
}
