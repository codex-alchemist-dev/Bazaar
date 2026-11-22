namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>
/// A veto over Soul attaches, called by <see cref="IMountManager.AttachAsync"/> for Soul mounts after the spec checks and before any
/// scan, canary, provider or guest work. The browser module implements it to refuse a Soul attach while an anonymous browser runs on
/// the VM (the anonymous profile must never share a guest with the cookie profile). An implementation refuses by throwing
/// <see cref="MountRefusedException"/> with <see cref="MountRefusalReason.AttachGuardRefused"/>; any other exception also stops the
/// attach (fail closed). The shipped default allows everything.
/// </summary>
public interface IMountAttachGuard
{
    Task CheckSoulAttachAsync(Guid vmId, CancellationToken ct);
}
