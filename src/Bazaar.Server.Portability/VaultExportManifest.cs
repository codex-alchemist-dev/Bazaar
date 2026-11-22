using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>One file listed in a Vault export manifest. <see cref="Path"/> is relative to the Vault export root.</summary>
public sealed record VaultExportManifestEntry(string Path, string Sha256, long Bytes);

/// <summary>
/// The manifest written beside a Vault export (doc 44 sections 4.1, 4.3 and 10 rule 6). It has no wall-clock time, so the
/// same repository state always gives the same bytes. <see cref="Plaintext"/> is always true until doc 33 Q6 is answered.
/// </summary>
public sealed record VaultExportManifest(
    string SchemaVersion,
    string HeadCommit,
    bool Plaintext,
    string PlaintextNotice,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<VaultExportManifestEntry> Files)
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Builds a manifest. Entries are sorted by path (ordinal); the plaintext flag and notice are always set.</summary>
    public static VaultExportManifest Create(string headCommit, IEnumerable<VaultExportManifestEntry> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommit);
        ArgumentNullException.ThrowIfNull(files);

        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new VaultExportManifest(
            VaultExportFormat.SchemaVersion,
            headCommit,
            Plaintext: true,
            PlaintextNotice: VaultExportFormat.PlaintextNotice,
            FileCount: ordered.Count,
            TotalBytes: ordered.Sum(f => f.Bytes),
            Files: ordered);
    }

    /// <summary>The manifest as UTF-8 JSON text with a final newline. The same input always gives the same text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, _options) + "\n";
}

/// <summary>Names and fixed text of the Vault export layout (doc 44 section 4.3).</summary>
public static class VaultExportFormat
{
    public const string SchemaVersion = "1.0.0";

    /// <summary>The manifest file, at the Vault export root.</summary>
    public const string ManifestFileName = "vault-manifest.json";

    /// <summary>The git bundle with all refs and full history, at the Vault export root.</summary>
    public const string BundleFileName = "repo.bundle";

    /// <summary>The folder that holds the plain working tree at the head commit.</summary>
    public const string TreeFolder = "tree";

    /// <summary>
    /// The plaintext warning every Vault manifest carries. Vault files are not encrypted under the KEK (doc 33 Q6), so the
    /// export is a plaintext copy until that question is answered (doc 44 section 10 rule 6).
    /// </summary>
    public const string PlaintextNotice =
        "Vault files are not encrypted under the KEK (doc 33 Q6, doc 44 section 10 rule 6). " +
        "This export is a plaintext copy of the Vault: every file at the head commit in tree/, and the full git history in repo.bundle. " +
        "Treat it as personal data wherever the Vault holds personal text. Erasure cannot reach a copy that has already been exported.";
}
