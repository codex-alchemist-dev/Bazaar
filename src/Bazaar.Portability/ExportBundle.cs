using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Portability;

/// <summary>One file in an export bundle. Path is relative, uses forward slashes, and never starts with a slash.</summary>
public sealed record BundleFile(string Path, byte[] Content);

/// <summary>
/// An in-memory export bundle (doc 44 section 5). <see cref="Files"/> holds every content file plus
/// manifest.json, sorted by path (ordinal). <see cref="Manifest"/> is the same manifest JSON text.
/// </summary>
public sealed record ExportBundle(string Manifest, IReadOnlyList<BundleFile> Files);

/// <summary>
/// Builds, verifies and writes export bundles in memory (doc 44 sections 5 and 6.1).
/// </summary>
/// <remarks>
/// NOT IMPLEMENTED YET (later slices): bundle encryption (Argon2id key plus AEAD archive, section 5),
/// the manifest signature manifest.sig and the public key (section 6.3), README.txt, splitting into
/// parts of at most 4 GiB, and resumable chunks. Nothing built here is encrypted or signed.
/// </remarks>
public static class ExportBundleWriter
{
    public const string SchemaVersion = "1.0.0";
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Builds a bundle. Files are validated, sorted by path (ordinal) and hashed; output is deterministic.</summary>
    public static ExportBundle Build(string instanceId, DateTimeOffset createdAt, IReadOnlyList<BundleFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(files);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(file.Content);
            ValidatePath(file.Path);
            // Ignore case as well: Windows hosts treat "A.txt" and "a.txt" as one file.
            if (!seen.Add(file.Path))
            {
                throw new ArgumentException("Duplicate path in bundle (case-insensitive): " + file.Path);
            }
        }

        var sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var entries = sorted
            .Select(f => new ManifestEntry(f.Path, Sha256Hex(f.Content), f.Content.LongLength))
            .ToList();
        var document = new ManifestDocument(
            SchemaVersion,
            instanceId,
            FormatUtc(createdAt),
            entries.Count,
            entries.Sum(e => e.Bytes),
            entries);

        var manifest = JsonSerializer.Serialize(document, ManifestOptions);
        var all = sorted
            .Append(new BundleFile(ManifestFileName, Encoding.UTF8.GetBytes(manifest)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return new ExportBundle(manifest, all);
    }

    /// <summary>
    /// Recomputes every SHA-256 and size against the manifest. Returns the problems found; an empty list means OK.
    /// Flags missing files, files not listed in the manifest, hash and size mismatches, and manifest errors.
    /// </summary>
    public static IReadOnlyList<string> Verify(ExportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var problems = new List<string>();

        var actual = new Dictionary<string, BundleFile>(StringComparer.Ordinal);
        foreach (var file in bundle.Files)
        {
            if (!actual.TryAdd(file.Path, file)) problems.Add("duplicate in bundle: " + file.Path);
        }

        if (!actual.TryGetValue(ManifestFileName, out var manifestFile))
        {
            problems.Add("manifest.json is missing from the bundle");
        }
        else if (!manifestFile.Content.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(bundle.Manifest)))
        {
            problems.Add("manifest.json differs from the bundle manifest");
        }

        var expected = new Dictionary<string, (string Sha256, long Bytes)>(StringComparer.Ordinal);
        long expectedTotal = 0;
        try
        {
            using var doc = JsonDocument.Parse(bundle.Manifest);
            if (!doc.RootElement.TryGetProperty("files", out var listed) || listed.ValueKind != JsonValueKind.Array)
            {
                problems.Add("manifest has no files array");
                return problems;
            }

            foreach (var entry in listed.EnumerateArray())
            {
                if (!TryReadEntry(entry, out var path, out var sha256, out var bytes) || !expected.TryAdd(path, (sha256, bytes)))
                {
                    problems.Add("manifest entry is malformed or duplicated");
                    continue;
                }
                expectedTotal += bytes;
            }

            if (doc.RootElement.TryGetProperty("fileCount", out var count) && count.GetInt32() != expected.Count
                || doc.RootElement.TryGetProperty("totalBytes", out var total) && total.GetInt64() != expectedTotal)
            {
                problems.Add("manifest totals do not match its file list");
            }
        }
        catch (JsonException)
        {
            problems.Add("manifest is not valid JSON");
            return problems;
        }
        catch (InvalidOperationException)
        {
            problems.Add("manifest totals have the wrong type");
            return problems;
        }

        foreach (var (path, (sha256, bytes)) in expected.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(path, out var file))
            {
                problems.Add("missing from bundle: " + path);
                continue;
            }
            if (file.Content.LongLength != bytes) problems.Add("size mismatch: " + path);
            if (Sha256Hex(file.Content) != sha256) problems.Add("sha256 mismatch: " + path);
        }

        foreach (var path in actual.Keys.Where(p => p != ManifestFileName).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!expected.ContainsKey(path)) problems.Add("not in manifest: " + path);
        }

        return problems;
    }

    /// <summary>
    /// Writes every bundle file under <paramref name="directory"/>, creating folders as needed. Refuses to write
    /// when the directory exists and is not empty, and refuses a bundle that fails <see cref="Verify"/>.
    /// </summary>
    public static void WriteTo(ExportBundle bundle, string directory)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var problems = Verify(bundle);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Bundle failed verification: " + string.Join("; ", problems));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException("Refusing to write: directory is not empty: " + root);
        }

        var prefix = root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var file in bundle.Files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Bundle path escapes the target directory: " + file.Path);
            }

            var folder = Path.GetDirectoryName(target);
            if (folder is not null) Directory.CreateDirectory(folder);
            File.WriteAllBytes(target, file.Content);
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Bundle path is empty.");
        if (path.Contains('\\', StringComparison.Ordinal)) throw new ArgumentException("Bundle path has a backslash: " + path);
        if (path.StartsWith('/') || Path.IsPathRooted(path)) throw new ArgumentException("Bundle path is absolute: " + path);
        if (path.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("Bundle path has a drive or scheme: " + path);
        if (path == ManifestFileName) throw new ArgumentException("Bundle path is reserved for the manifest: " + path);

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) throw new ArgumentException("Bundle path has an empty segment: " + path);
            if (segment is "." or "..") throw new ArgumentException("Bundle path has a traversal segment: " + path);
        }
    }

    private static bool TryReadEntry(JsonElement entry, out string path, out string sha256, out long bytes)
    {
        path = string.Empty;
        sha256 = string.Empty;
        bytes = 0;
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (!entry.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) return false;
        if (!entry.TryGetProperty("sha256", out var h) || h.ValueKind != JsonValueKind.String) return false;
        if (!entry.TryGetProperty("bytes", out var b) || !b.TryGetInt64(out bytes)) return false;
        path = p.GetString()!;
        sha256 = h.GetString()!;
        return true;
    }

    private static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal sealed record ManifestEntry(string Path, string Sha256, long Bytes);

    internal sealed record ManifestDocument(
        string SchemaVersion,
        string InstanceId,
        string CreatedAt,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<ManifestEntry> Files);
}
