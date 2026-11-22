namespace Bazaar.Community.Tools;

/// <summary>Default identity source: knows no agent, so <c>community.propose_post</c> refuses until the host binds a real one.</summary>
internal sealed class UnboundCommunityAgentIdentitySource : ICommunityAgentIdentitySource
{
    public Task<CommunityAgentIdentity?> FindAsync(Guid agentId, string platform, CancellationToken ct = default) =>
        Task.FromResult<CommunityAgentIdentity?>(null);
}
