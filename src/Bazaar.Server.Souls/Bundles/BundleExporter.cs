using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls;

namespace Bazaar.Server.Souls.Bundles;

// TODO(component): move to AddBazaarSouls() when the component model (PR #125) merges.
/// <summary>
/// Builds a Soul bundle (docs/design/07-soul-system.md, "Export"): the charter, optional canonical preferences, the allowed
/// extra files, <c>manifest.json</c> with SHA-256 digests, sizes and the exclusion list, and a detached signature over the
/// exact manifest bytes. Output is deterministic: files sort by ordinal path, JSON is written in a fixed field order, and the
/// timestamp comes from the caller. Refuses the whole export when any exported file matches the secret scan.
/// </summary>
public sealed class BundleExporter
{
    /// <summary>Bundle format version written to the manifest.</summary>
    public const int FormatVersion = 1;

    /// <summary>The charter file inside a bundle.</summary>
    public const string CharterPath = "charter.md";

    /// <summary>The canonical preferences file, written only when preferences are included.</summary>
    public const string PreferencesPath = "preferences.json";

    /// <summary>The manifest file.</summary>
    public const string ManifestPath = "manifest.json";

    /// <summary>The detached signature file.</summary>
    public const string SignaturePath = "signature.json";

    private static readonly string[] _reservedPaths = [CharterPath, PreferencesPath, ManifestPath, SignaturePath];

    private readonly IBundleSigner _signer;

    /// <summary>Creates an exporter that signs with <paramref name="signer"/>.</summary>
    public BundleExporter(IBundleSigner signer)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    /// <summary>
    /// Exports one Soul version. Throws <see cref="ArgumentException"/> for bad input (hash mismatch, invalid or duplicate path,
    /// invalid category, empty exporter) and <see cref="BundleSecretScanException"/> when an exported file matches the scan.
    /// </summary>
    public BundleExportResult Export(BundleExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Version);
        ArgumentNullException.ThrowIfNull(request.ExtraFiles);
        ValidateExporter(request.Exporter);

        var version = request.Version;
        var charterSha = Sha256Hex(request.CharterBytes.Span);
        if (!string.Equals(charterSha, version.CharterSha256, StringComparison.Ordinal))
            throw new ArgumentException("The charter bytes do not match the version's CharterSha256.", nameof(request));

        var preferences = SoulCanonicalJson.Canonicalize(request.PreferencesJson);
        if (!string.Equals(SoulVersioningService.ContentHash(charterSha, preferences), version.ContentSha256, StringComparison.Ordinal))
            throw new ArgumentException("The charter and preferences do not match the version's ContentSha256.", nameof(request));

        var exclusions = new SortedSet<string>(StringComparer.Ordinal);
        var files = new List<BundleOutputFile> { new(CharterPath, request.CharterBytes) };

        if (request.IncludePreferences)
            files.Add(new BundleOutputFile(PreferencesPath, Encoding.UTF8.GetBytes(preferences)));
        else
            exclusions.Add(BundleCategories.Preferences);

        var seen = new HashSet<string>(_reservedPaths, StringComparer.Ordinal);
        foreach (var extra in request.ExtraFiles)
        {
            ArgumentNullException.ThrowIfNull(extra);
            BundlePaths.Validate(extra.Path);
            if (!BundleCategories.IsValidName(extra.Category))
                throw new ArgumentException($"Category '{extra.Category}' is not a valid exclusion category name.", nameof(request));
            if (!seen.Add(extra.Path))
                throw new ArgumentException($"Bundle path '{extra.Path}' appears more than once.", nameof(request));

            if (BundleCategories.IsExported(extra.Category))
                files.Add(new BundleOutputFile(extra.Path, extra.Content));
            else
                exclusions.Add(extra.Category);
        }

        var findings = new List<SecretFinding>();
        foreach (var file in files)
            findings.AddRange(BundleSecretScanner.Scan(file.Path, file.Content.Span));
        if (findings.Count > 0)
            throw new BundleSecretScanException(findings);

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var entries = files
            .Select(f => new BundleFile(f.Path, Sha256Hex(f.Content.Span), f.Content.Length))
            .ToList();
        var manifest = new BundleManifest(
            FormatVersion,
            version.SoulId,
            version.Version,
            version.ContentSha256,
            request.Exporter,
            request.ExportedAt,
            entries,
            exclusions.ToList());

        var manifestBytes = WriteManifest(manifest);
        var signature = _signer.Sign(manifestBytes);
        var signatureBytes = WriteSignature(signature);

        files.Add(new BundleOutputFile(ManifestPath, manifestBytes));
        files.Add(new BundleOutputFile(SignaturePath, signatureBytes));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new BundleExportResult(manifest, signature, files);
    }

    private static void ValidateExporter(string exporter)
    {
        if (string.IsNullOrWhiteSpace(exporter) || exporter.Length > 200 || exporter.Any(char.IsControl))
            throw new ArgumentException("The exporter must be 1 to 200 printable characters.", nameof(exporter));
    }

    private static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Writes the manifest with a fixed field order and no insignificant whitespace.</summary>
    private static byte[] WriteManifest(BundleManifest manifest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("soulId", manifest.SoulId);
            writer.WriteNumber("soulVersion", manifest.SoulVersion);
            writer.WriteString("contentSha256", manifest.ContentSha256);
            writer.WriteString("exporter", manifest.Exporter);
            writer.WriteString("exportedAt", manifest.ExportedAt.ToUniversalTime());
            writer.WriteStartArray("files");
            foreach (var file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("size", file.Size);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("exclusions");
            foreach (var category in manifest.Exclusions)
                writer.WriteStringValue(category);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteSignature(BundleSignature signature)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("keyId", signature.KeyId);
            writer.WriteString("algorithm", signature.Algorithm);
            writer.WriteString("signatureBase64", signature.SignatureBase64);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
