using Bazaar.PrivacyAudit;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IPersonSuspension"/> over <see cref="IRefreshCredentialStore"/>. The state change and the device and key revocations are one
/// store call, so a suspended person never has a live device. The audit entries follow the change; a state that did not change writes none.
/// </summary>
public sealed class PersonSuspensionService(IRefreshCredentialStore store, PrivacyAuditRecorder audit, TimeProvider time) : IPersonSuspension
{
    public async Task<PersonSuspensionOutcome> SuspendAsync(Guid humanId, PrincipalRef by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);
        var human = await store.FindHumanAsync(humanId, ct).ConfigureAwait(false);
        if (human is null)
            return PersonSuspensionOutcome.NotFound;
        if (human.Role == "owner" || (by.Kind == "human" && by.Id == humanId))
            return PersonSuspensionOutcome.NotAllowed;

        var result = await store.SuspendHumanAsync(humanId, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (!result.Changed)
            return PersonSuspensionOutcome.NoChange;

        await audit.PersonSuspendedAsync(ActorOf(by), humanId.ToString("D"), result.RevokedDevices.Count, ct).ConfigureAwait(false);
        foreach (var device in result.RevokedDevices)
            await audit.DeviceRevokedAsync(ActorOf(by), device.ToString("D"), "person_suspended", ct).ConfigureAwait(false);
        return PersonSuspensionOutcome.Done;
    }

    public async Task<PersonSuspensionOutcome> UnsuspendAsync(Guid humanId, PrincipalRef by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (await store.FindHumanAsync(humanId, ct).ConfigureAwait(false) is null)
            return PersonSuspensionOutcome.NotFound;
        if (!await store.UnsuspendHumanAsync(humanId, ct).ConfigureAwait(false))
            return PersonSuspensionOutcome.NoChange;

        await audit.PersonUnsuspendedAsync(ActorOf(by), humanId.ToString("D"), ct).ConfigureAwait(false);
        return PersonSuspensionOutcome.Done;
    }

    private static string ActorOf(PrincipalRef by) => by.Kind + ":" + by.Id.ToString("D");
}
