using System.Globalization;
using System.Runtime.Versioning;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// The unprivileged reader of a <c>bitlocker-vhdx</c> data volume (doc 63 sections 3.1 and 4). It cannot read BitLocker state
/// itself, so it fails closed:
/// <list type="bullet">
/// <item><c>Mounted</c> is true only when the data root is itself a volume mount point (kernel32 answers for the exact folder).</item>
/// <item><c>VolumeId</c> is that volume's canonical GUID.</item>
/// <item><c>Encrypted</c>, <c>ProtectionOn</c> and <c>Cipher</c> come only from the status channel. An unavailable channel, an
/// error answer or an answer for another GUID gives <c>Encrypted</c> false, which the guard refuses.</item>
/// </list>
/// A locked volume reports <c>Mounted</c> true and <c>Encrypted</c> true, with the real protection state: a locked volume cannot
/// be a usable data root, but it is the correct volume and it is encrypted. Reads fail with 0x80310000, so the guard's marker
/// check refuses the start. Reporting it as not encrypted would send the operator to the wrong fix.
/// </summary>
public sealed class BitLockerVhdxDataVolumeProvider : IDataVolumeProvider
{
    private readonly IMountPointReader _mounts;
    private readonly IVolumeStatusChannel _channel;

    internal BitLockerVhdxDataVolumeProvider(IMountPointReader mounts, IVolumeStatusChannel channel)
    {
        _mounts = mounts;
        _channel = channel;
    }

    public string Kind => BitLockerVhdxDataVolumeOperator.ProviderKind;

    /// <summary>The provider with the real kernel32 reader and the named-pipe channel.</summary>
    [SupportedOSPlatform("windows")]
    public static BitLockerVhdxDataVolumeProvider CreateDefault() =>
        new(new NativeMountPointReader(), new NamedPipeVolumeStatusChannel());

    public async ValueTask<DataVolumeStatus> InspectAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var guid = _mounts.TryGetVolumeGuid(dataRoot);
        if (guid is null)
            return new DataVolumeStatus(Mounted: false, VolumeId: null, Encrypted: false, ProtectionOn: false, Cipher: null);

        var reply = await _channel.GetStatusAsync(guid, cancellationToken);
        if (reply is null || !string.Equals(reply.Volume, guid, StringComparison.Ordinal))
            return new DataVolumeStatus(Mounted: true, guid, Encrypted: false, ProtectionOn: false, Cipher: null);

        return new DataVolumeStatus(
            Mounted: true,
            guid,
            Encrypted: reply.ConversionComplete,
            ProtectionOn: reply.ProtectionOn,
            Cipher: reply.ConversionComplete ? CipherName(reply.EncryptionMethod) : null);
    }

    /// <summary>Maps the BitLocker method number to a cipher name. Only 7 meets the floor; the others keep a distinct name so the refusal says what was found.</summary>
    internal static string CipherName(int method) => method switch
    {
        BitLockerValues.XtsAes256 => DataVolumeCiphers.XtsAes256,
        0 => DataVolumeCiphers.None,
        _ => string.Create(CultureInfo.InvariantCulture, $"bitlocker-method-{method}"),
    };
}
