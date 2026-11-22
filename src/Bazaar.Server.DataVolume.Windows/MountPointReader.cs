using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// Reads the volume GUID of a folder mount. It works without elevation and also on a locked volume (doc 63 section 3.1).
/// Returns the canonical GUID, or null when the folder is not a volume mount point.
/// </summary>
internal interface IMountPointReader
{
    string? TryGetVolumeGuid(string folder);
}

[SupportedOSPlatform("windows")]
internal sealed partial class NativeMountPointReader : IMountPointReader
{
    // \\?\Volume{guid}\ is 49 characters plus the terminator; 64 leaves room.
    private const int _bufferChars = 64;

    public string? TryGetVolumeGuid(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        // The API requires the trailing backslash and answers only for the exact mount point, not for a folder inside a volume.
        var mountPoint = folder.EndsWith('\\') ? folder : folder + "\\";
        var buffer = new char[_bufferChars];
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length))
            return null;

        var end = Array.IndexOf(buffer, '\0');
        var name = new string(buffer, 0, end < 0 ? buffer.Length : end);
        return VolumeGuid.TryCanonicalize(name, out var canonical) ? canonical : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, int bufferLength);
}
