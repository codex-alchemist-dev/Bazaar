using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Security.Taint;
using SessionTaint = Bazaar.Server.Security.Taint.SessionTaint;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Production <see cref="IPushSessionRecords"/> over the running turns (doc 17, "Host-side sources"). The session is the running
/// turn of the pushing agent that was woken by the push's group chat. Nothing is defaulted: no such turn means no record, so the push is
/// denied. The approval grant is always absent until the approval store can look one up by agent, session and tool.
/// </summary>
public sealed class RunningTurnPushSessionRecords : IPushSessionRecords
{
    private readonly IRunningTurnIndex _turns;
    private readonly ISessionTaintStore _taint;
    private readonly TimeSpan _readTimeout;

    /// <summary>Creates the records source.</summary>
    public RunningTurnPushSessionRecords(IRunningTurnIndex turns, ISessionTaintStore taint, TimeSpan readTimeout)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(taint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);
        _turns = turns;
        _taint = taint;
        _readTimeout = readTimeout;
    }

    /// <inheritdoc />
    public async ValueTask<PushSessionRecord?> FindAsync(PushGateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.AgentId, out var agentId) || string.IsNullOrEmpty(request.GcId))
        {
            return null;
        }

        var turn = _turns.ForAgent(agentId)
            .Where(t => string.Equals(t.TriggerGcId, request.GcId, StringComparison.Ordinal))
            .OrderByDescending(t => t.ToolAllowlist.Contains(PermissionGatePushGate.ToolName, StringComparer.Ordinal))
            .ThenBy(t => t.Depth)
            .ThenBy(t => t.TurnId)
            .FirstOrDefault();
        if (turn is null)
        {
            return null;
        }

        var role = turn.ToolAllowlist.Contains(PermissionGatePushGate.ToolName, StringComparer.Ordinal)
            ? ToolSessionRole.Actor
            : ToolSessionRole.Reader;

        string trust;
        try
        {
            var taint = await _taint.LoadAsync(turn.SessionId, cancellationToken).WaitAsync(_readTimeout, cancellationToken).ConfigureAwait(false);
            trust = SourceTrustLabel(taint);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            trust = "external";
        }

        return new PushSessionRecord(turn.SessionId.ToString("D"), role, trust);
    }

    /// <summary>The least trusted label among the taint sources, using the wire names; "agent" when there are none and the session is not Hard.</summary>
    internal static string SourceTrustLabel(SessionTaint? taint)
    {
        if (taint is null)
        {
            return "external";
        }

        if (taint.Sources.IsDefaultOrEmpty)
        {
            return taint.State == TaintState.Hard ? "external" : "agent";
        }

        return taint.Sources.Max(s => Rank(s.Trust)) switch
        {
            5 => "quarantined",
            4 => "external",
            3 => "agent",
            2 => "member-human",
            1 => "system",
            _ => "owner",
        };
    }

    private static int Rank(ContentTrust trust) => trust switch
    {
        ContentTrust.Owner => 0,
        ContentTrust.System => 1,
        ContentTrust.MemberHuman => 2,
        ContentTrust.Agent => 3,
        ContentTrust.External => 4,
        _ => 5,
    };
}
