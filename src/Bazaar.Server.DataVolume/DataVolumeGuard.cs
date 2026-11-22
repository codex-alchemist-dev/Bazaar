using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// The start guard (doc 63 section 5). It reads the descriptor, asks the provider what is mounted at the data root, and
/// verifies the marker. It only reads: it never creates a key, a descriptor, a marker or a folder, so a missing data
/// root is a refusal and not a fresh install.
/// </summary>
public sealed class DataVolumeGuard(DataVolumeOptions options, IKekProvider kek, IEnumerable<IDataVolumeProvider> providers) : IDataVolumeGuard
{
    public async ValueTask<DataVolumeVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!IsAbsolute(options.BootRoot) || !IsAbsolute(options.DataRoot))
            throw Refuse(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.BootRootKey} and {DataVolumeOptions.DataRootKey} must be set to absolute paths. Run setup.");

        if (string.IsNullOrWhiteSpace(options.InstallId) || string.IsNullOrWhiteSpace(options.VolumeId))
            throw Refuse(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.InstallIdKey} and {DataVolumeOptions.VolumeIdKey} are not set. Run setup.");

        DataVolumeDescriptor? descriptor;
        try
        {
            descriptor = await DescriptorStore.ReadAsync(options.DescriptorPath, cancellationToken);
        }
        catch (InvalidDataException e)
        {
            throw Refuse(DataVolumeRefusal.IdentityMismatch, "The volume descriptor is damaged.", e);
        }

        if (descriptor is null)
            throw Refuse(DataVolumeRefusal.NotInitialised, $"No volume descriptor at '{options.DescriptorPath}'. The host does not create one at start. Run setup, or mount the boot root.");

        if (!Same(descriptor.InstallId, options.InstallId) || !Same(descriptor.VolumeId, options.VolumeId))
            throw Refuse(DataVolumeRefusal.IdentityMismatch, "The volume descriptor belongs to another install or volume than host config names.");

        if (descriptor.InstanceClass != options.InstanceClass)
            throw Refuse(DataVolumeRefusal.IdentityMismatch, "The instance class in host config differs from the descriptor. An instance class never changes.");

        if (!Same(descriptor.KekId, kek.KekId))
            throw Refuse(DataVolumeRefusal.IdentityMismatch, $"The descriptor was wrapped under KEK '{descriptor.KekId}', and this host opened '{kek.KekId}'.");

        var provider = providers.FirstOrDefault(p => Same(p.Kind, descriptor.ProviderKind))
            ?? throw Refuse(DataVolumeRefusal.NotInitialised, $"The volume provider '{descriptor.ProviderKind}' is not available on this host.");

        if (!Directory.Exists(options.DataRoot))
            throw Refuse(DataVolumeRefusal.NotMounted, $"The data root '{options.DataRoot}' does not exist. The host does not create it at start.");

        var status = await provider.InspectAsync(options.DataRoot, cancellationToken);
        if (!status.Mounted)
            throw Refuse(DataVolumeRefusal.NotMounted, $"The data root '{options.DataRoot}' is not the mount point of the data volume.");

        if (provider is DevPlainDataVolumeProvider)
        {
            // dev-plain answers only the "is encrypted" question, and only for a development instance (doc 63 section 8).
            if (descriptor.InstanceClass != InstanceClass.Development || !options.IsDevelopmentEnvironment)
                throw Refuse(DataVolumeRefusal.NotEncrypted, "The dev-plain provider is accepted only for a development instance running with DOTNET_ENVIRONMENT=Development.");
        }
        else
        {
            if (!status.Encrypted || !status.ProtectionOn)
                throw Refuse(DataVolumeRefusal.NotEncrypted, $"The volume at '{options.DataRoot}' is not fully encrypted with protection on.");

            if (!DataVolumeCiphers.MeetsFloor(status.Cipher) || !DataVolumeCiphers.MeetsFloor(descriptor.Cipher))
                throw Refuse(DataVolumeRefusal.NotEncrypted, $"The volume cipher '{status.Cipher}' is below the floor ({DataVolumeCiphers.XtsAes256}).");

            if (status.VolumeId is null || !Same(status.VolumeId, descriptor.VolumeId))
                throw Refuse(DataVolumeRefusal.IdentityMismatch, "The volume mounted at the data root is not the volume this install created.");
        }

        byte[] markerKey;
        try
        {
            markerKey = await VolumeKeyCustody.UnwrapMarkerKeyAsync(kek, descriptor, cancellationToken);
        }
        catch (KekUnwrapException e)
        {
            throw Refuse(DataVolumeRefusal.IdentityMismatch, "The volume descriptor does not unwrap under this host's KEK. It was changed or belongs to another host.", e);
        }

        VolumeMarkerContent? marker;
        try
        {
            marker = await VolumeMarker.ReadVerifiedAsync(options.MarkerPath, markerKey, cancellationToken);
        }
        finally
        {
            Array.Clear(markerKey);
        }

        if (marker is null)
            throw Refuse(DataVolumeRefusal.MarkerInvalid, $"The marker '{options.MarkerPath}' is missing or does not verify. The folder is not the volume setup created.");

        if (!Same(marker.InstallId, descriptor.InstallId) || !Same(marker.VolumeId, descriptor.VolumeId) || marker.InstanceClass != descriptor.InstanceClass)
            throw Refuse(DataVolumeRefusal.MarkerInvalid, "The marker belongs to another install or volume.");

        return new DataVolumeVerification(descriptor, status, marker.PostgresSystemIdentifier);
    }

    public void EnsureInsideDataRoot(IEnumerable<string> paths) => DataRootPaths.EnsureInside(options.DataRoot, paths);

    private static bool IsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static DataVolumeRefusedException Refuse(DataVolumeRefusal reason, string message, Exception? inner = null) => new(reason, message, inner);
}
