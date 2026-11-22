using System.Globalization;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>
/// The Linux <c>luks2</c> status reader (doc 63 sections 3 and 4). It runs unprivileged and uses only world-readable kernel
/// files: <c>/proc/self/mountinfo</c> finds the mount at the data root, <c>/sys/dev/block/&lt;maj:min&gt;/dm/uuid</c> says whether
/// it is a LUKS2 mapping and carries the LUKS UUID. The cipher is not exposed without root (<c>dmsetup table</c> needs it), so
/// it is read from the LUKS2 header of the backing container, which the operator makes group-readable by the service account;
/// when the header cannot be read the cipher is null and the guard refuses. The header is not a proof against a forger who
/// can already rewrite the container, but it must match the active mapping's UUID, and a container the service account can
/// only read cannot be changed by it.
/// </summary>
public sealed class Luks2DataVolumeProvider : IDataVolumeProvider
{
    public const string ProviderKind = "luks2";

    private const string _mountInfoPath = "/proc/self/mountinfo";
    private static readonly DataVolumeStatus _notMounted = new(Mounted: false, VolumeId: null, Encrypted: false, ProtectionOn: false, Cipher: null);

    private readonly ILinuxVolumeFiles _files;

    public Luks2DataVolumeProvider()
        : this(new SystemLinuxVolumeFiles())
    {
    }

    internal Luks2DataVolumeProvider(ILinuxVolumeFiles files)
    {
        _files = files;
    }

    public string Kind => ProviderKind;

    public ValueTask<DataVolumeStatus> InspectAsync(string dataRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(Inspect(dataRoot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any surprise reads as "not a mounted volume"; the guard then refuses.
            return ValueTask.FromResult(_notMounted);
        }
    }

    private DataVolumeStatus Inspect(string dataRoot)
    {
        var resolved = _files.ResolvePath(dataRoot);
        var mountInfo = _files.ReadAllText(_mountInfoPath);
        if (mountInfo is null)
            return _notMounted;

        // The data root is the root of a mounted volume only when a mount sits exactly there and shows that volume's own root.
        var mount = MountInfoParser.FindMountAt(mountInfo, resolved);
        if (mount is null || mount.Root != "/")
            return _notMounted;

        var device = "/sys/dev/block/" + mount.MajorMinor;
        var luksUuid = DmUuid.TryGetLuks2Uuid(_files.ReadAllText(device + "/dm/uuid"));
        if (luksUuid is null)
            return new DataVolumeStatus(Mounted: true, VolumeId: null, Encrypted: false, ProtectionOn: false, Cipher: null);

        return new DataVolumeStatus(Mounted: true, luksUuid, Encrypted: true, ProtectionOn: true, ReadBackingCipher(device, luksUuid));
    }

    private string? ReadBackingCipher(string device, string luksUuid)
    {
        // One backing device only: a mapping stacked on several is not what setup creates.
        var slaves = _files.ListDirectory(device + "/slaves");
        if (slaves.Count != 1)
            return null;

        var name = slaves[0];
        if (name.Length == 0 || name.AsSpan().IndexOfAny('/', '\0') >= 0 || name is "." or "..")
            return null;

        var backingFile = _files.ReadAllText($"/sys/block/{name}/loop/backing_file")?.Trim();
        var path = string.IsNullOrEmpty(backingFile) ? "/dev/" + name : backingFile;
        if (path[0] != '/' || path.AsSpan().IndexOf('\0') >= 0)
            return null;

        var first = _files.ReadPrefix(path, Luks2Header.BinaryHeaderSize);
        if (first is null || Luks2Header.TryGetHeaderSize(first) is not { } size)
            return null;

        var header = _files.ReadPrefix(path, size);
        if (header is null || header.Length < size)
            return null;

        // The header must belong to the mapping that is mounted.
        var headerUuid = Luks2Header.ReadUuid(header);
        return string.Equals(headerUuid, luksUuid, StringComparison.Ordinal) ? Luks2Header.ReadCipher(header) : null;
    }

    /// <summary>The mapping name for a volume id: <c>bazaar-data-</c> plus the first 8 hex digits of the LUKS UUID.</summary>
    internal static string MappingName(string volumeId)
    {
        if (!Guid.TryParse(volumeId, out var guid))
            throw new ArgumentException("The volume id must be a LUKS UUID.", nameof(volumeId));
        return "bazaar-data-" + guid.ToString("N", CultureInfo.InvariantCulture)[..8];
    }
}
