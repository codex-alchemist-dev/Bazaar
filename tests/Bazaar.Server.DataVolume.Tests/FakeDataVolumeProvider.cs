namespace Bazaar.Server.DataVolume.Tests;

/// <summary>A provider whose answers a test sets. It stands in for a real encrypted volume.</summary>
public sealed class FakeDataVolumeProvider(string volumeId) : IDataVolumeProvider
{
    public const string ProviderKind = "fake-encrypted";

    public string Kind => ProviderKind;

    public DataVolumeStatus Status { get; set; } = new(Mounted: true, volumeId, Encrypted: true, ProtectionOn: true, DataVolumeCiphers.XtsAes256);

    public ValueTask<DataVolumeStatus> InspectAsync(string dataRoot, CancellationToken cancellationToken) => ValueTask.FromResult(Status);
}
