using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.DataVolume;

/// <summary>The marker file as stored. <see cref="Mac"/> is HMAC-SHA-256 over the other fields under the marker key.</summary>
internal sealed record VolumeMarkerFile(
    int FormatVersion,
    string InstallId,
    string VolumeId,
    InstanceClass InstanceClass,
    long CreatedAtUnixMs,
    string? PostgresSystemIdentifier,
    byte[] Mac);

/// <summary>The verified content of a marker.</summary>
public sealed record VolumeMarkerContent(string InstallId, string VolumeId, InstanceClass InstanceClass, DateTimeOffset CreatedAt, string? PostgresSystemIdentifier);

/// <summary>
/// The marker inside the data root (doc 63 section 2). It proves that the file system mounted there is the one setup
/// created: an empty mount point, another volume or a plain folder with the same name has no marker that verifies.
/// </summary>
public static class VolumeMarker
{
    public const string FileName = ".bazaar-volume";

    private const int _formatVersion = 1;

    /// <summary>Writes the marker. Setup calls it once after creating the volume, and again to record the cluster's system identifier.</summary>
    public static ValueTask WriteAsync(string path, VolumeMarkerContent content, ReadOnlySpan<byte> markerKey, CancellationToken cancellationToken)
    {
        var createdAt = content.CreatedAt.ToUnixTimeMilliseconds();
        var file = new VolumeMarkerFile(
            _formatVersion,
            content.InstallId,
            content.VolumeId,
            content.InstanceClass,
            createdAt,
            content.PostgresSystemIdentifier,
            Mac(markerKey, content.InstallId, content.VolumeId, content.InstanceClass, createdAt, content.PostgresSystemIdentifier));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DataVolumeJsonContext.Default.VolumeMarkerFile);
        return AtomicFile.WriteAsync(path, bytes, overwrite: true, cancellationToken);
    }

    /// <summary>Reads and verifies the marker. Returns null when it is missing, malformed or its MAC does not verify; the caller refuses either way.</summary>
    public static async ValueTask<VolumeMarkerContent?> ReadVerifiedAsync(string path, ReadOnlyMemory<byte> markerKey, CancellationToken cancellationToken)
    {
        VolumeMarkerFile? file;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            file = JsonSerializer.Deserialize(bytes, DataVolumeJsonContext.Default.VolumeMarkerFile);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        if (file is null || file.FormatVersion != _formatVersion || file.InstallId is null || file.VolumeId is null || file.Mac is null)
            return null;

        var expected = Mac(markerKey.Span, file.InstallId, file.VolumeId, file.InstanceClass, file.CreatedAtUnixMs, file.PostgresSystemIdentifier);
        if (!CryptographicOperations.FixedTimeEquals(expected, file.Mac))
            return null;

        return new VolumeMarkerContent(
            file.InstallId,
            file.VolumeId,
            file.InstanceClass,
            DateTimeOffset.FromUnixTimeMilliseconds(file.CreatedAtUnixMs),
            file.PostgresSystemIdentifier);
    }

    private static byte[] Mac(ReadOnlySpan<byte> key, string installId, string volumeId, InstanceClass instanceClass, long createdAtUnixMs, string? systemIdentifier)
    {
        // Length-prefixed fields, so no value can be shifted into its neighbour.
        var builder = new StringBuilder("bazaar.datavolume.marker.v1");
        foreach (var field in (string[])[installId, volumeId, instanceClass.ToString(), createdAtUnixMs.ToString(CultureInfo.InvariantCulture), systemIdentifier ?? string.Empty])
        {
            builder.Append('|').Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        }

        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(builder.ToString()));
    }
}
