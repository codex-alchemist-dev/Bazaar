using System.Text;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Portability;

/// <summary>
/// Writes a git bundle of a Vault repository (doc 44 section 4.3). The host implements it, for example with the git CLI.
/// </summary>
public interface IVaultBundleWriter
{
    /// <summary>
    /// Writes a git bundle with every ref and the full history of <paramref name="repoPath"/> to <paramref name="destination"/>.
    /// It does not dispose the stream.
    /// </summary>
    Task WriteBundleAsync(string repoPath, Stream destination, CancellationToken ct);
}

/// <summary>
/// Exports a Vault repository as <c>repo.bundle</c>, a plain <c>tree/</c> of its files at one commit, and
/// <c>vault-manifest.json</c> with the plaintext warning (doc 44 sections 4.3, 6.1 and 10 rule 6).
/// </summary>
/// <remarks>
/// <para>
/// Reads go through <see cref="IGitProvider"/> (doc 18). The bundle is streamed to the sink, not built in memory. Each
/// Vault file is read as text at the commit and written with its bytes unchanged, so wiki-links and line endings survive
/// (doc 18 round-trip rule). Each file's content is held only while that file is written.
/// </para>
/// <para>
/// Output is deterministic: tree paths are validated and sorted ordinal, and the manifest has no timestamp. Every path is
/// validated before the first file is written, so a bad path leaves the sink untouched.
/// </para>
/// <para>
/// Scope: the exporter writes the whole Vault. Filtering by principal (doc 18 access rules) is not built, and the bundle
/// carries full history, so a filtered export needs a decision on history first.
/// </para>
/// </remarks>
public sealed class VaultExporter(IGitProvider git, IVaultBundleWriter bundles)
{
    /// <summary>
    /// Exports the Vault at <paramref name="commitish"/> (a branch, tag or SHA) of <paramref name="repoPath"/> into
    /// <paramref name="sink"/> and returns the manifest that was written.
    /// </summary>
    public async Task<VaultExportManifest> ExportAsync(string repoPath, string commitish, IVaultExportSink sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitish);
        ArgumentNullException.ThrowIfNull(sink);

        var head = await git.ResolveCommitAsync(repoPath, commitish, ct);
        var treePaths = ValidateTreePaths(await git.ListFilesAsync(repoPath, head, ct));

        var entries = new List<VaultExportManifestEntry>(treePaths.Count + 1)
        {
            await WriteBundleAsync(repoPath, sink, ct),
        };

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var path in treePaths)
        {
            ct.ThrowIfCancellationRequested();
            var text = await git.ReadTextAsync(repoPath, head, path, ct);
            var exported = VaultExportFormat.TreeFolder + "/" + path;
            entries.Add(await WriteFileAsync(sink, exported, encoding.GetBytes(text), ct));
        }

        var manifest = VaultExportManifest.Create(head, entries);
        var manifestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(manifest.ToJson());
        await WriteFileAsync(sink, VaultExportFormat.ManifestFileName, manifestBytes, ct);
        return manifest;
    }

    private async Task<VaultExportManifestEntry> WriteBundleAsync(string repoPath, IVaultExportSink sink, CancellationToken ct)
    {
        await using var file = await sink.OpenWriteAsync(VaultExportFormat.BundleFileName, ct);
        var digest = new DigestingWriteStream(file);
        await bundles.WriteBundleAsync(repoPath, digest, ct);
        await digest.FlushAsync(ct);
        if (digest.BytesWritten == 0)
        {
            throw new InvalidOperationException("The bundle writer produced no bytes for " + repoPath);
        }
        return new VaultExportManifestEntry(VaultExportFormat.BundleFileName, digest.Sha256Hex(), digest.BytesWritten);
    }

    private static async Task<VaultExportManifestEntry> WriteFileAsync(IVaultExportSink sink, string path, byte[] content, CancellationToken ct)
    {
        await using var file = await sink.OpenWriteAsync(path, ct);
        var digest = new DigestingWriteStream(file);
        await digest.WriteAsync(content, ct);
        await digest.FlushAsync(ct);
        return new VaultExportManifestEntry(path, digest.Sha256Hex(), digest.BytesWritten);
    }

    /// <summary>
    /// Validates every tree path as it will appear under <c>tree/</c>, refuses paths that differ only by case (a Windows host
    /// would write them as one file), and returns the paths sorted ordinal.
    /// </summary>
    private static List<string> ValidateTreePaths(IReadOnlyList<string> paths)
    {
        var seenFolded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var path in ordered)
        {
            VaultExportPaths.Validate(VaultExportFormat.TreeFolder + "/" + path);
            if (!seenFolded.Add(path))
            {
                throw new ArgumentException("Two Vault paths differ only by case: " + path);
            }
        }
        return ordered;
    }
}
