using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.DataVolume;

/// <summary>Reads and writes the volume descriptor file. Writes are atomic and never replace an existing descriptor.</summary>
public static class DescriptorStore
{
    /// <summary>The only descriptor format this build reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>Reads the descriptor, or returns null when the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a version 1 descriptor.</exception>
    public static async ValueTask<DataVolumeDescriptor?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        DataVolumeDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize(bytes, DataVolumeJsonContext.Default.DataVolumeDescriptor);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The volume descriptor is not valid JSON.", e);
        }

        if (descriptor is null
            || descriptor.FormatVersion != FormatVersion
            || string.IsNullOrWhiteSpace(descriptor.InstallId)
            || string.IsNullOrWhiteSpace(descriptor.VolumeId)
            || string.IsNullOrWhiteSpace(descriptor.ProviderKind)
            || string.IsNullOrWhiteSpace(descriptor.KekId)
            || descriptor.WrappedVolumeKey is null
            || descriptor.WrappedMarkerKey is null or { Length: 0 })
        {
            throw new InvalidDataException("The volume descriptor is incomplete or has an unknown format version.");
        }

        return descriptor;
    }

    /// <summary>Writes a new descriptor. Fails when one already exists: a descriptor is replaced only by an explicit rotation.</summary>
    public static ValueTask WriteNewAsync(string path, DataVolumeDescriptor descriptor, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(descriptor, DataVolumeJsonContext.Default.DataVolumeDescriptor), overwrite: false, cancellationToken);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DataVolumeDescriptor))]
[JsonSerializable(typeof(VolumeMarkerFile))]
internal sealed partial class DataVolumeJsonContext : JsonSerializerContext;
