using System.Text.Json;

namespace Bazaar.Server.Backup.Manifest;

/// <summary>
/// JSON layout of a manifest file (UTF-8), written and read through the source-generated <see cref="ManifestJsonContext"/>.
/// Doc 33 fixes the digest (SHA-256) but not the file format. Shape:
/// <code>
/// {"format":"bazaar-backup-manifest","version":1,"hashAlgorithm":"SHA-256",
///  "entries":[{"path":"a.txt","length":3,"sha256":"&lt;64 lower-case hex&gt;"}]}
/// </code>
/// Entries must be unique and sorted by path (ordinal), so one manifest has one byte sequence.
/// </summary>
public static class ManifestCodec
{
    /// <summary>Value of the "format" member; identifies the file as a Bazaar backup manifest.</summary>
    public const string FormatName = "bazaar-backup-manifest";

    public static byte[] Serialize(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = new List<ManifestEntryDto>(manifest.Entries.Count);
        string? previous = null;
        foreach (var entry in manifest.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var path = BundlePath.Normalize(entry.Path);
            if (previous is not null && string.CompareOrdinal(previous, path) >= 0)
            {
                throw new ArgumentException($"Manifest entries must be unique and sorted by path; '{path}' is out of order.", nameof(manifest));
            }

            var problem = CheckEntry(path, entry.Length, entry.Sha256);
            if (problem is not null)
            {
                throw new ArgumentException(problem, nameof(manifest));
            }

            previous = path;
            entries.Add(new ManifestEntryDto { Path = path, Length = entry.Length, Sha256 = entry.Sha256 });
        }

        var document = new ManifestDocumentDto
        {
            Format = FormatName,
            Version = BackupManifest.FormatVersion,
            HashAlgorithm = BackupManifest.HashAlgorithm,
            Entries = entries,
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, ManifestJsonContext.Default.ManifestDocumentDto);
    }

    /// <summary>Parses manifest bytes. Returns a manifest, or a parse error describing the first problem found.</summary>
    public static ManifestParseResult TryParse(ReadOnlySpan<byte> bytes)
    {
        ManifestDocumentDto? document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, ManifestJsonContext.Default.ManifestDocumentDto);
        }
        catch (JsonException ex)
        {
            return ManifestParseResult.Fail($"Manifest is not valid manifest JSON: {ex.Message}");
        }

        if (document is null)
        {
            return ManifestParseResult.Fail("Manifest is empty.");
        }

        if (document.Format != FormatName)
        {
            return ManifestParseResult.Fail($"Not a Bazaar backup manifest (format '{document.Format}').");
        }

        if (document.Version != BackupManifest.FormatVersion)
        {
            return ManifestParseResult.Fail($"Unsupported manifest version {document.Version}; this build reads version {BackupManifest.FormatVersion}.");
        }

        if (document.HashAlgorithm != BackupManifest.HashAlgorithm)
        {
            return ManifestParseResult.Fail($"Unsupported hash algorithm '{document.HashAlgorithm}'; expected {BackupManifest.HashAlgorithm}.");
        }

        if (document.Entries is null)
        {
            return ManifestParseResult.Fail("Manifest has no entries list.");
        }

        var entries = new List<BackupManifestEntry>(document.Entries.Count);
        string? previous = null;
        for (var i = 0; i < document.Entries.Count; i++)
        {
            var dto = document.Entries[i];
            if (dto is null || dto.Path is null || dto.Sha256 is null)
            {
                return ManifestParseResult.Fail($"Entry {i + 1} is missing its path or sha256.");
            }

            string path;
            try
            {
                path = BundlePath.Normalize(dto.Path);
            }
            catch (ArgumentException ex)
            {
                return ManifestParseResult.Fail($"Entry {i + 1}: {ex.Message}");
            }

            if (path != dto.Path)
            {
                return ManifestParseResult.Fail($"Entry {i + 1} path is not in normalised form.");
            }

            var problem = CheckEntry(path, dto.Length, dto.Sha256);
            if (problem is not null)
            {
                return ManifestParseResult.Fail($"Entry {i + 1}: {problem}");
            }

            if (previous is not null && string.CompareOrdinal(previous, path) >= 0)
            {
                return ManifestParseResult.Fail($"Entry {i + 1} is out of order or duplicates a path.");
            }

            previous = path;
            entries.Add(new BackupManifestEntry(path, dto.Length, dto.Sha256));
        }

        return ManifestParseResult.Ok(new BackupManifest(entries));
    }

    /// <summary>Returns a description of the first problem with one entry, or null when the entry is well formed.</summary>
    private static string? CheckEntry(string path, long length, string sha256)
    {
        if (length < 0)
        {
            return $"'{path}' has a negative length.";
        }

        if (sha256.Length != 64 || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return $"'{path}' has a hash that is not 64 lower-case hex digits.";
        }

        return null;
    }
}

/// <summary>Outcome of <see cref="ManifestCodec.TryParse"/>: a manifest, or an error message.</summary>
public sealed record ManifestParseResult(BackupManifest? Manifest, string? Error)
{
    public static ManifestParseResult Ok(BackupManifest manifest) => new(manifest, null);

    public static ManifestParseResult Fail(string error) => new(null, error);
}
