using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.DataVolume;

/// <summary>What setup knows about the volume it has just created and mounted.</summary>
public sealed record NewDataVolume(
    string InstallId,
    string VolumeId,
    string ProviderKind,
    string ContainerKind,
    string ContainerPath,
    string Cipher,
    InstanceClass InstanceClass);

/// <summary>
/// Writes the descriptor and the marker for a new volume (doc 63 section 10). Only setup and the restore wizard call
/// this, after the provider has created and mounted the volume. It refuses to replace an existing descriptor, so it
/// cannot be used to re-key an install by accident.
/// </summary>
public static class DataVolumeInitializer
{
    /// <param name="volumeKey">The 32-byte key the provider used as the volume's protector; empty for <c>dev-plain</c>.</param>
    public static async ValueTask<DataVolumeDescriptor> InitializeAsync(
        DataVolumeOptions options,
        NewDataVolume volume,
        ReadOnlyMemory<byte> volumeKey,
        IKekProvider kek,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(time);

        var plain = volume.ProviderKind == DevPlainDataVolumeProvider.ProviderKind;
        if (plain && volume.InstanceClass != InstanceClass.Development)
            throw new ArgumentException("The dev-plain provider is only for a development instance.", nameof(volume));
        if (!plain && volumeKey.Length != VolumeKeyCustody.KeySize)
            throw new ArgumentException("The volume key must be 32 bytes.", nameof(volumeKey));
        if (!plain && !DataVolumeCiphers.MeetsFloor(volume.Cipher))
            throw new ArgumentException($"The cipher must be {DataVolumeCiphers.XtsAes256}.", nameof(volume));
        if (!Directory.Exists(options.DataRoot))
            throw new DirectoryNotFoundException($"The data root '{options.DataRoot}' must be mounted before the volume is initialised.");
        if (File.Exists(options.DescriptorPath))
            throw new InvalidOperationException($"A volume descriptor already exists at '{options.DescriptorPath}'.");

        var now = time.GetUtcNow();
        var markerKey = VolumeKeyCustody.NewKey();
        try
        {
            var descriptor = new DataVolumeDescriptor(
                DescriptorStore.FormatVersion,
                volume.InstallId,
                volume.VolumeId,
                volume.ProviderKind,
                volume.ContainerKind,
                volume.ContainerPath,
                volume.Cipher,
                volume.InstanceClass,
                kek.KekId,
                await VolumeKeyCustody.WrapVolumeKeyAsync(kek, volume.InstallId, volume.VolumeId, volumeKey, cancellationToken),
                await VolumeKeyCustody.WrapMarkerKeyAsync(kek, volume.InstallId, volume.VolumeId, markerKey, cancellationToken),
                now);

            // Marker first: a crash between the two writes leaves no descriptor, so the next setup run starts clean.
            await VolumeMarker.WriteAsync(
                options.MarkerPath,
                new VolumeMarkerContent(volume.InstallId, volume.VolumeId, volume.InstanceClass, now, PostgresSystemIdentifier: null),
                markerKey,
                cancellationToken);
            await DescriptorStore.WriteNewAsync(options.DescriptorPath, descriptor, cancellationToken);
            return descriptor;
        }
        finally
        {
            Array.Clear(markerKey);
        }
    }

    /// <summary>Records the cluster's system identifier in the marker after <c>initdb</c>, so a swapped cluster is detected at start.</summary>
    public static async ValueTask RecordPostgresSystemIdentifierAsync(
        DataVolumeOptions options,
        DataVolumeDescriptor descriptor,
        string systemIdentifier,
        IKekProvider kek,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemIdentifier);
        var markerKey = await VolumeKeyCustody.UnwrapMarkerKeyAsync(kek, descriptor, cancellationToken);
        try
        {
            var current = await VolumeMarker.ReadVerifiedAsync(options.MarkerPath, markerKey, cancellationToken)
                ?? throw new DataVolumeRefusedException(DataVolumeRefusal.MarkerInvalid, "The marker is missing or does not verify.");
            await VolumeMarker.WriteAsync(options.MarkerPath, current with { PostgresSystemIdentifier = systemIdentifier }, markerKey, cancellationToken);
        }
        finally
        {
            Array.Clear(markerKey);
        }
    }
}
