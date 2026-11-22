namespace Bazaar.Server.DataVolume;

/// <summary>
/// The <c>dev-plain</c> provider (doc 63 section 8): a plain folder with no encryption, for contributors and CI. The
/// guard accepts it only for an instance that setup created with class <c>development</c> and only when the process
/// runs in the Development environment. The marker and identity checks are the same as for a real volume.
/// </summary>
public sealed class DevPlainDataVolumeProvider : IDataVolumeProvider
{
    public const string ProviderKind = "dev-plain";

    public string Kind => ProviderKind;

    public ValueTask<DataVolumeStatus> InspectAsync(string dataRoot, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DataVolumeStatus(
            Mounted: Directory.Exists(dataRoot),
            VolumeId: null,
            Encrypted: false,
            ProtectionOn: false,
            Cipher: DataVolumeCiphers.None));
}
