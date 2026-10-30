using System.Globalization;
using System.Text;

namespace Bazaar.Server.Backup.Manifest;

/// <summary>
/// Text layout of a manifest file (UTF-8, LF line endings, one entry per line). Chosen here because doc 33 fixes the
/// digest (SHA-256) but not the file format. Layout:
/// <code>
/// bazaar-backup-manifest/1 SHA-256
/// sha256 &lt;64 lower-case hex&gt; &lt;length in bytes&gt; &lt;normalised path&gt;
/// </code>
/// Entries must be unique and sorted by path (ordinal), so one manifest has one byte sequence.
/// </summary>
public static class ManifestCodec
{
    private const string _header = "bazaar-backup-manifest/1 " + BackupManifest.HashAlgorithm;

    public static byte[] Serialize(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var sb = new StringBuilder();
        sb.Append(_header).Append('\n');
        foreach (var entry in manifest.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var path = BundlePath.Normalize(entry.Path);
            sb.Append("sha256 ")
                .Append(entry.Sha256)
                .Append(' ')
                .Append(entry.Length.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(path)
                .Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Parses manifest bytes. Returns a manifest, or a parse error describing the first problem found.</summary>
    public static ManifestParseResult TryParse(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ManifestParseResult.Fail("Manifest is not valid UTF-8.");
        }

        if (text.Length == 0 || text[^1] != '\n')
        {
            return ManifestParseResult.Fail("Manifest must end with a line feed.");
        }

        var lines = text[..^1].Split('\n');
        if (lines[0] != _header)
        {
            return ManifestParseResult.Fail($"Unsupported manifest header: '{lines[0]}'.");
        }

        var entries = new List<BackupManifestEntry>(lines.Length - 1);
        string? previous = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(' ', 4);
            if (fields.Length != 4 || fields[0] != "sha256")
            {
                return ManifestParseResult.Fail($"Line {i + 1} is not an 'sha256 <hash> <length> <path>' entry.");
            }

            var hash = fields[1];
            if (hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return ManifestParseResult.Fail($"Line {i + 1} has a hash that is not 64 lower-case hex digits.");
            }

            if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                return ManifestParseResult.Fail($"Line {i + 1} has a length that is not a non-negative integer.");
            }

            string path;
            try
            {
                path = BundlePath.Normalize(fields[3]);
            }
            catch (ArgumentException ex)
            {
                return ManifestParseResult.Fail($"Line {i + 1}: {ex.Message}");
            }

            if (path != fields[3])
            {
                return ManifestParseResult.Fail($"Line {i + 1} path is not in normalised form.");
            }

            if (previous is not null && string.CompareOrdinal(previous, path) >= 0)
            {
                return ManifestParseResult.Fail($"Line {i + 1} is out of order or duplicates a path.");
            }

            previous = path;
            entries.Add(new BackupManifestEntry(path, length, hash));
        }

        return ManifestParseResult.Ok(new BackupManifest(entries));
    }
}

/// <summary>Outcome of <see cref="ManifestCodec.TryParse"/>: a manifest, or an error message.</summary>
public sealed record ManifestParseResult(BackupManifest? Manifest, string? Error)
{
    public static ManifestParseResult Ok(BackupManifest manifest) => new(manifest, null);

    public static ManifestParseResult Fail(string error) => new(null, error);
}
