using Bazaar.Contracts.Hive;

namespace Bazaar.Server.Hive.Abstractions;

/// <summary>
/// Server-side dispatch for <c>hive.grant.set</c> and <c>hive.grant.revoke</c> (doc 25, Hive grant wire request). Validation and
/// owner refusals come back as <see cref="HiveGrantChangeReply"/> with <c>Accepted = false</c>; only cancellation and
/// infrastructure failures throw.
/// </summary>
public interface IHiveGrantWireHandler
{
    /// <summary>Handles one wire request from an authenticated caller.</summary>
    Task<HiveGrantChangeReply> HandleAsync(string wireType, HiveGrantChangeRequest request, HiveCaller caller, CancellationToken ct);
}
