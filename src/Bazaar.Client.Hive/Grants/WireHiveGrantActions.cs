using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Hive.Grants;

/// <summary>
/// Grant and revoke over the wire (doc 25, "Hive grant wire request"): sends <c>hive.grant.set</c> or
/// <c>hive.grant.revoke</c> for one pair. The server decides authority; owner approval stays server-side, and the result
/// carries <see cref="HiveGrantResult.ApprovalRequired"/> so the pane never reports a queued change as granted. A lost
/// connection surfaces as the thrown exception, so the pane keeps the matrix as it was.
/// </summary>
public sealed class WireHiveGrantActions(IWireRequester requests) : IHiveGrantActions
{
    /// <inheritdoc />
    public Task<HiveGrantResult> GrantAsync(HiveGrantKey key, CancellationToken ct) =>
        SendAsync(WireTypes.HiveGrantSet, key, ct);

    /// <inheritdoc />
    public Task<HiveGrantResult> RevokeAsync(HiveGrantKey key, CancellationToken ct) =>
        SendAsync(WireTypes.HiveGrantRevoke, key, ct);

    private async Task<HiveGrantResult> SendAsync(string type, HiveGrantKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var reply = await requests.RequestAsync<HiveGrantChangeRequest, HiveGrantChangeReply>(
            type, new HiveGrantChangeRequest(key.FromProject, key.ToProject, key.Kind), ct).ConfigureAwait(false);
        return new HiveGrantResult(reply.Accepted, reply.Reason ?? string.Empty, reply.ApprovalRequired);
    }
}
