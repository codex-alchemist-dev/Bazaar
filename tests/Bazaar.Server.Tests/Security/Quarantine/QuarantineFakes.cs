using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Tests.Security.Quarantine;

/// <summary>Records every revoke call, in order.</summary>
internal sealed class RecordingAgentTokenRevoker : IAgentTokenRevoker
{
    public List<string> Revoked { get; } = [];

    public Task RevokeAsync(string agentId, CancellationToken ct = default)
    {
        Revoked.Add(agentId);
        return Task.CompletedTask;
    }
}

/// <summary>Records every pause call, in order.</summary>
internal sealed class RecordingVmPauser : IVmPauser
{
    public List<string> Paused { get; } = [];

    public Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        Paused.Add(agentId);
        return Task.CompletedTask;
    }
}
