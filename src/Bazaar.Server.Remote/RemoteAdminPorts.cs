using Bazaar.Client.Tui.Dashboard;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IAdminUserDirectory"/> over the <c>human</c> table (doc 51 section 5, doc 32). Only people are listed: agents have no
/// row here, so <see cref="AdminUser.IsAgent"/> is always false. A person who is not <c>active</c> (suspended or removed) shows as revoked.
/// </summary>
public sealed class RemoteAdminUserDirectory(IIdentityDirectoryStore store) : IAdminUserDirectory
{
    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default)
    {
        var humans = await store.ListHumansAsync(ct).ConfigureAwait(false);
        return [.. humans.Select(h => new AdminUser(h.Id, h.DisplayName, h.Role, IsAgent: false, Revoked: h.Status != "active"))];
    }
}

/// <summary>
/// <see cref="IAdminDeviceDirectory"/> over the <c>device</c> table. <see cref="AdminDevice.LastSeen"/> is the issue time of the
/// device's newest refresh credential. No credential, hash or key reaches the record.
/// </summary>
public sealed class RemoteAdminDeviceDirectory(IIdentityDirectoryStore store) : IAdminDeviceDirectory
{
    public async Task<IReadOnlyList<AdminDevice>> ListAsync(CancellationToken ct = default)
    {
        var devices = await store.ListDevicesAsync(ct).ConfigureAwait(false);
        return [.. devices.Select(d => new AdminDevice(d.Device.Id, d.Device.HumanId, d.Device.Label, d.LastActivity, d.Device.RevokedAt is not null))];
    }
}

/// <summary>
/// <see cref="IOpsDeviceRevoker"/> through <see cref="ITokenService.RevokeDeviceAsync"/>, the path an owner revoking a device from a
/// client takes (doc 32). The refusal reason is fixed text; an exception message is never passed on.
/// </summary>
public sealed class RemoteOpsDeviceRevoker(ITokenService tokens) : IOpsDeviceRevoker
{
    public async Task<OpsWriteResult> RevokeDeviceAsync(Guid actorPrincipalId, Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            await tokens.RevokeDeviceAsync(deviceId, new PrincipalRef(actorPrincipalId, "human"), ct).ConfigureAwait(false);
            return new OpsWriteResult(true);
        }
        catch (KeyNotFoundException)
        {
            return new OpsWriteResult(false, "No such device.");
        }
    }
}

/// <summary>
/// <see cref="IOpsMemberRevoker"/> as a suspension (PROPOSED, doc 32 person suspension): the person's devices and approval keys are revoked
/// and their status becomes <c>suspended</c>, which is reversible with <see cref="RemoteOpsMemberRestorer"/>. The refusal text is fixed;
/// an exception message is never passed on.
/// </summary>
public sealed class RemoteOpsMemberRevoker(IPersonSuspension suspension) : IOpsMemberRevoker
{
    public async Task<OpsWriteResult> RevokeMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default) =>
        Result(await suspension.SuspendAsync(principalId, new PrincipalRef(actorPrincipalId, "human"), ct).ConfigureAwait(false), "suspended");

    internal static OpsWriteResult Result(PersonSuspensionOutcome outcome, string state) => outcome switch
    {
        PersonSuspensionOutcome.Done => new OpsWriteResult(true),
        PersonSuspensionOutcome.NotFound => new OpsWriteResult(false, "No such person."),
        PersonSuspensionOutcome.NotAllowed => new OpsWriteResult(false, "The owner and your own account cannot be suspended."),
        _ => new OpsWriteResult(false, "That person is not in a state that can be " + state + "."),
    };
}

/// <summary><see cref="IOpsMemberRestorer"/> through <see cref="IPersonSuspension.UnsuspendAsync"/>. Revoked devices stay revoked.</summary>
public sealed class RemoteOpsMemberRestorer(IPersonSuspension suspension) : IOpsMemberRestorer
{
    public async Task<OpsWriteResult> RestoreMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default) =>
        RemoteOpsMemberRevoker.Result(
            await suspension.UnsuspendAsync(principalId, new PrincipalRef(actorPrincipalId, "human"), ct).ConfigureAwait(false),
            "unsuspended");
}

/// <summary>
/// <see cref="IAdminRevocationCheck"/> over the identity tables: a panel session ends when its person is suspended or removed or its
/// device is revoked. An unknown person or device is not revoked here (the owner's local gate has no device row); sign-in decides who gets in.
/// </summary>
public sealed class RemoteAdminRevocationCheck(IRefreshCredentialStore store) : IAdminRevocationCheck
{
    public async Task<bool> IsRevokedAsync(Guid principalId, Guid deviceId, CancellationToken ct = default)
    {
        if (await store.FindHumanAsync(principalId, ct).ConfigureAwait(false) is { Status: not "active" })
            return true;
        return deviceId != Guid.Empty && await store.FindDeviceAsync(deviceId, ct).ConfigureAwait(false) is { RevokedAt: not null };
    }
}
