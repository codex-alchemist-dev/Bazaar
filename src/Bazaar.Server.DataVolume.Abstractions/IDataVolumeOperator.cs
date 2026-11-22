namespace Bazaar.Server.DataVolume;

/// <summary>What setup asks an operator to create (doc 63 sections 3 and 7).</summary>
/// <param name="ContainerKind"><c>file</c> (a container file at <paramref name="ContainerPath"/>) or <c>device</c> (an existing block device or partition).</param>
/// <param name="ContainerPath">The container file to create, or the device to use.</param>
/// <param name="MountPath">The data root: an existing empty folder that becomes the volume's mount point.</param>
/// <param name="MaxSizeBytes">The size ceiling of a thin <c>file</c> container; ignored for <c>device</c>.</param>
/// <param name="ServiceAccount">The unprivileged account that must own or be granted the mounted file system.</param>
public sealed record DataVolumeCreateRequest(string ContainerKind, string ContainerPath, string MountPath, long MaxSizeBytes, string ServiceAccount);

/// <summary>
/// The privileged half of a data volume provider (doc 63 section 4): it creates, opens and closes the encrypted volume.
/// Only the volume unit and setup call it, as administrator or root. The unprivileged host uses
/// <see cref="IDataVolumeProvider"/> and never holds the volume key. Implementations never write the key to disk and
/// never log it; callers clear the key after the call.
/// </summary>
public interface IDataVolumeOperator
{
    /// <summary>The provider kind stored in the descriptor; equals the matching <see cref="IDataVolumeProvider.Kind"/>.</summary>
    string Kind { get; }

    /// <summary>
    /// Creates the container, encrypts it with <paramref name="volumeKey"/> as its only protector (cipher
    /// <see cref="DataVolumeCiphers.XtsAes256"/>), creates the file system, mounts it at the request's mount path and
    /// returns the OS volume id (BitLocker volume GUID or LUKS UUID). Fails when the container already exists.
    /// </summary>
    ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken);

    /// <summary>Attaches, unlocks and mounts the volume the descriptor names at <paramref name="mountPath"/>. Opening an already open volume succeeds.</summary>
    ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken);

    /// <summary>
    /// Unmounts, locks and detaches the volume. Refuses with <see cref="InvalidOperationException"/> while a process still
    /// has files open under <paramref name="mountPath"/>: a forced lock under a running PostgreSQL kills the cluster
    /// process (doc 63 section 3.1), so there is no force option.
    /// </summary>
    ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken);
}
