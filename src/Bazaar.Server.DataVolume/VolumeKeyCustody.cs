using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// Wraps and unwraps the volume key and the marker key through the KEK provider (doc 63 sections 2 and 6). The
/// associated data binds each wrap to the install, the volume and the KEK, so a wrap copied into another descriptor fails.
/// </summary>
public static class VolumeKeyCustody
{
    /// <summary>Size of the volume key and of the marker key.</summary>
    public const int KeySize = 32;

    private const string _volumeKeyLabel = "bazaar.datavolume.key.v1";
    private const string _markerKeyLabel = "bazaar.datavolume.marker.v1";

    public static ValueTask<byte[]> WrapVolumeKeyAsync(IKekProvider kek, string installId, string volumeId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
        kek.WrapAsync(key, AssociatedData(_volumeKeyLabel, installId, volumeId, kek.KekId), cancellationToken);

    public static ValueTask<byte[]> WrapMarkerKeyAsync(IKekProvider kek, string installId, string volumeId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
        kek.WrapAsync(key, AssociatedData(_markerKeyLabel, installId, volumeId, kek.KekId), cancellationToken);

    /// <summary>Unwraps the volume key. Only the privileged volume unit calls this; the host never holds the volume key.</summary>
    /// <exception cref="KekUnwrapException">The descriptor, the KEK or the ids do not match the wrap.</exception>
    public static ValueTask<byte[]> UnwrapVolumeKeyAsync(IKekProvider kek, DataVolumeDescriptor descriptor, CancellationToken cancellationToken) =>
        kek.UnwrapAsync(descriptor.WrappedVolumeKey, AssociatedData(_volumeKeyLabel, descriptor.InstallId, descriptor.VolumeId, descriptor.KekId), cancellationToken);

    /// <summary>Unwraps the marker key, the only key the host holds.</summary>
    /// <exception cref="KekUnwrapException">The descriptor, the KEK or the ids do not match the wrap.</exception>
    public static ValueTask<byte[]> UnwrapMarkerKeyAsync(IKekProvider kek, DataVolumeDescriptor descriptor, CancellationToken cancellationToken) =>
        kek.UnwrapAsync(descriptor.WrappedMarkerKey, AssociatedData(_markerKeyLabel, descriptor.InstallId, descriptor.VolumeId, descriptor.KekId), cancellationToken);

    /// <summary>A fresh random 32-byte key. The caller clears it after use.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static byte[] AssociatedData(string label, string installId, string volumeId, string kekId) =>
        Encoding.UTF8.GetBytes(string.Join('|', label, installId, volumeId, kekId));
}
