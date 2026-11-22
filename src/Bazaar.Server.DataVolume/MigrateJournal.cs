using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.DataVolume;

/// <summary>The last step <c>storage migrate</c> finished. A re-run continues after it and never repeats a creating step.</summary>
internal enum MigratePhase
{
    /// <summary>Preconditions passed and the journal exists. Nothing else has been created.</summary>
    Started,

    /// <summary>The legacy KEK is copied to the boot root, byte for byte, and opens there.</summary>
    KekCopied,

    /// <summary>The volume operator is about to be (or was) called. If the descriptor is not there, the volume key is gone and a person must clean up.</summary>
    VolumeCreating,

    /// <summary>The volume exists, the descriptor and marker are written and the guard passed.</summary>
    VolumeCreated,

    /// <summary>Every file is copied and the manifest is written.</summary>
    Copied,

    /// <summary>The copy is verified. The plain copy is still in place.</summary>
    Verified,
}

/// <summary>
/// <c>&lt;boot root&gt;/volume/migrate.json</c>: where a migration stands. It holds paths, ids and the phase, never key material.
/// </summary>
internal sealed record MigrateJournal(
    int FormatVersion,
    string From,
    string BootRoot,
    string DataRoot,
    string InstallId,
    string? VolumeId,
    string ProviderKind,
    string ContainerKind,
    string ContainerPath,
    MigratePhase Phase,
    DateTimeOffset Started,
    DateTimeOffset Updated);

internal static class MigrateJournalStore
{
    public const int FormatVersion = 1;

    public static string PathFor(DataVolumeOptions options) => Path.Combine(options.BootRoot, "volume", "migrate.json");

    public static async ValueTask<MigrateJournal?> ReadAsync(string path, CancellationToken cancellationToken)
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

        try
        {
            var journal = JsonSerializer.Deserialize(bytes, MigrateJsonContext.Default.MigrateJournal);
            if (journal is null || journal.FormatVersion != FormatVersion || string.IsNullOrWhiteSpace(journal.From) || string.IsNullOrWhiteSpace(journal.InstallId))
                throw new InvalidDataException("The migration journal is incomplete or has an unknown format version.");

            return journal;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The migration journal is not valid JSON.", e);
        }
    }

    public static ValueTask WriteAsync(string path, MigrateJournal journal, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(journal, MigrateJsonContext.Default.MigrateJournal), overwrite: true, cancellationToken);
}

/// <summary>Relative path, length and SHA-256 of one copied file.</summary>
internal sealed record ManifestFile(string Path, long Length, string Sha256);

/// <summary>What was copied: written into the mount while copying, read again by the verify step.</summary>
internal sealed record MigrateManifest(int FormatVersion, List<ManifestFile> Files, List<string> Directories);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MigrateJournal))]
[JsonSerializable(typeof(MigrateManifest))]
internal sealed partial class MigrateJsonContext : JsonSerializerContext;
