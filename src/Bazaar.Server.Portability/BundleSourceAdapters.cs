namespace Bazaar.Server.Portability;

/// <summary>
/// Feeds the Vault exporter into the bundle assembler (doc 44 sections 4.3, 5 and 6, work package 5). The exporter writes
/// <c>repo.bundle</c>, <c>tree/</c> and <c>vault-manifest.json</c> relative to its own root; this source places them under
/// <c>vault/</c>, the folder of the doc 44 section 5 layout. The assembler hashes every byte, so the exporter's own manifest is
/// kept as an ordinary bundle file.
/// </summary>
public sealed class VaultBundleSource(VaultExporter exporter, string repoPath, string commitish) : IBundleSource
{
    /// <summary>The folder the Vault files go under in the bundle.</summary>
    public const string Folder = "vault/";

    public const string SourceId = "vault.v1";

    public string Id => SourceId;

    /// <summary>The exporter's manifest from the last run, or null before the first run.</summary>
    public VaultExportManifest? Manifest { get; private set; }

    public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Manifest = await exporter.ExportAsync(repoPath, commitish, new PrefixedSink(sink, Folder), ct);
    }

    private sealed class PrefixedSink(IExportFileSink inner, string prefix) : IVaultExportSink
    {
        public Task<Stream> OpenWriteAsync(string relativePath, CancellationToken ct)
        {
            // Validate before prefixing, so a bad exporter path cannot be hidden by the folder name.
            VaultExportPaths.Validate(relativePath);
            return inner.OpenAsync(prefix + relativePath, ct);
        }
    }
}

/// <summary>
/// Feeds the personal-field exporter into the bundle assembler (doc 44 sections 4.6, 5 and 10). The exporter returns its files
/// (already under <c>personal/</c>, including <c>personal/index.json</c>); this source writes them in the order given.
/// </summary>
/// <remarks>
/// The exporter audits the export when it runs (doc 44 section 10), before the assembler has finished. If assembly fails later,
/// the audit entry already exists. The plaintext notice for the owner is kept in <see cref="PlaintextNotice"/>.
/// </remarks>
public sealed class PersonalFieldBundleSource(PersonalFieldExporter exporter, PersonalExportScope scope) : IPlaintextBundleSource
{
    public const string SourceId = "personal.v1";

    public string Id => SourceId;

    /// <summary>The owner notice from the last run when plaintext was written, otherwise null.</summary>
    public string? PlaintextNotice { get; private set; }

    /// <summary>The plaintext files from the last run (the assembler flags them in the manifest), otherwise empty.</summary>
    public IReadOnlyList<string> PlaintextPersonalPaths { get; private set; } = [];

    public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var result = await exporter.ExportAsync(scope, ct);
        await BundleFileWriter.WriteAllAsync(sink, result.Files, ct);
        PlaintextNotice = result.PlaintextNotice;
        PlaintextPersonalPaths = result.PlaintextPaths ?? [];
    }
}

/// <summary>
/// Feeds the audit exporter into the bundle assembler (doc 44 sections 4.6 and 6.1, work package 8). The exporter runs its replay
/// self-check on the bytes it produced and throws <see cref="AuditExportException"/> on any failure, which aborts the bundle.
/// Checkpoint signatures are copied untouched.
/// </summary>
public sealed class AuditBundleSource(AuditExporter exporter, long? startCheckpointLastSeq = null) : IBundleSource
{
    public const string SourceId = "audit.v1";

    public string Id => SourceId;

    /// <summary>The self-check result from the last run, or null before the first run.</summary>
    public AuditReplayResult? SelfCheck { get; private set; }

    public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var result = await exporter.ExportAsync(startCheckpointLastSeq, ct);
        await BundleFileWriter.WriteAllAsync(sink, result.Files, ct);
        SelfCheck = result.SelfCheck;
    }
}

internal static class BundleFileWriter
{
    /// <summary>Writes each file to the sink in turn, one stream at a time. Paths are checked by the sink.</summary>
    internal static async Task WriteAllAsync(IExportFileSink sink, IReadOnlyList<BundleFile> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await sink.OpenAsync(file.Path, ct);
            await stream.WriteAsync(file.Content, ct);
        }
    }
}
