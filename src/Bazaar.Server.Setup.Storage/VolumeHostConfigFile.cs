using System.Text.Json;
using Bazaar.Server.DataVolume;

namespace Bazaar.Server.Setup.Storage;

/// <summary>
/// The host config file the volume bootstrap writes on the boot root (doc 63 section 10): <c>config/volume.json</c> with the
/// data root, the install id, the volume id and the instance class. The host reads it as configuration below environment
/// variables. It holds no key and no secret.
/// </summary>
public static class VolumeHostConfigFile
{
    /// <summary>Path of the file relative to the boot root.</summary>
    public const string RelativePath = "config/volume.json";

    /// <summary>The file for <paramref name="bootRoot"/>.</summary>
    public static string PathFor(string bootRoot) => Path.Combine(bootRoot, "config", "volume.json");

    /// <summary>Writes the file through a temporary file and a rename, so a crash never leaves half a file.</summary>
    public static async Task WriteAsync(string bootRoot, string dataRoot, DataVolumeDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(descriptor);

        var path = PathFor(bootRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("Bazaar");
            writer.WriteStartObject("Host");
            writer.WriteString("DataRoot", dataRoot);
            writer.WriteString("InstallId", descriptor.InstallId);
            writer.WriteString("VolumeId", descriptor.VolumeId);
            writer.WriteString("InstanceClass", descriptor.InstanceClass.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, buffer.ToArray(), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
}
