using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// One registered source of bundle files (doc 44 sections 5 and 9, work package 2). It writes its files through the sink, one at a
/// time, and the assembler hashes every byte as it is written. Wrap an existing exporter with <see cref="DelegateBundleSource"/>.
/// </summary>
public interface IBundleSource
{
    /// <summary>Stable id, for example <c>gc.jsonl.v1</c>. Sources run in ordinal order of this id, so the run is deterministic.</summary>
    string Id { get; }

    /// <summary>Writes this source's files to the sink. Throwing aborts the whole bundle.</summary>
    Task ExportAsync(IExportFileSink sink, CancellationToken ct);
}

/// <summary>An <see cref="IBundleSource"/> built from a delegate, so an exporter's <c>ExportAsync(..., sink, ct)</c> plugs in with a lambda.</summary>
public sealed class DelegateBundleSource(string id, Func<IExportFileSink, CancellationToken, Task> export) : IBundleSource
{
    public string Id { get; } = id;

    public Task ExportAsync(IExportFileSink sink, CancellationToken ct) => export(sink, ct);
}

/// <summary>
/// An <see cref="IBundleSource"/> that can write files holding decrypted personal data (doc 44 section 4.6). The assembler reads
/// <see cref="PlaintextPersonalPaths"/> after the source has run and flags those files in <c>manifest.json</c> and in README.txt.
/// </summary>
public interface IPlaintextBundleSource : IBundleSource
{
    /// <summary>Bundle paths this source wrote that hold plaintext personal data, valid after <c>ExportAsync</c>. Empty when none.</summary>
    IReadOnlyList<string> PlaintextPersonalPaths { get; }
}

/// <summary>What the assembler is asked to produce. Doc 44 is silent on several of these, so each is a parameter.</summary>
public sealed record BundleAssemblyOptions
{
    /// <summary>The instance id written to the manifest.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The creation time written to the manifest (UTC, to the second). The caller supplies it so output stays deterministic.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The instance export key (ECDSA P-256) that signs the manifest. Held by the Identity Service in production.</summary>
    public required ECDsa SigningKey { get; init; }

    /// <summary>
    /// The bundle directory to create. It must not exist or must be empty. <see cref="ExportBundleAssembler.DirectoryName"/> builds the
    /// layout name of doc 44 section 5.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// When set, the signed bundle is also written as one encrypted archive at this path (doc 44 section 5, wrapping), using
    /// <see cref="Passphrase"/>. Null writes the directory only.
    /// </summary>
    public string? EncryptedArchivePath { get; init; }

    /// <summary>Passphrase for <see cref="EncryptedArchivePath"/>. Required when that path is set.</summary>
    public string? Passphrase { get; init; }

    /// <summary>Argon2id cost for the archive. Null uses the shipped default.</summary>
    public Argon2Parameters? KdfParameters { get; init; }

    /// <summary>Copy buffer size for hashing and file writes, in bytes. Exposed so tests can force many chunks.</summary>
    public int BufferSize { get; init; } = 81_920;
}

/// <summary>The outcome of a completed assembly.</summary>
public sealed record BundleAssemblyResult(
    string Directory,
    string Manifest,
    IReadOnlyList<ExportedFile> Files,
    string? EncryptedArchivePath);

/// <summary>
/// Runs the registered sources, writes each file once with its SHA-256 and size computed as the bytes stream out, writes
/// manifest.json, signs it, and optionally encrypts the result (doc 44 sections 5, 6.3 and 16 item 2).
/// </summary>
/// <remarks>
/// The bundle is built in a hidden sibling directory and renamed into place only when everything succeeded. A failing
/// source, a bad path, a duplicate path or a cancellation deletes the working directory, so no partial bundle is left. The manifest
/// has the same shape as <see cref="ExportBundleWriter"/> builds, so <see cref="ExportBundleWriter.Verify"/> and
/// <see cref="BundleSigner.Verify"/> accept it. Encryption goes through <see cref="ExportBundleCipher"/>, which is not streaming:
/// the archive step holds the whole bundle in memory.
/// README.txt is generated after the sources have run (<see cref="BundleReadme"/>) and listed in the manifest like any file, since the
/// verifier fails a file the manifest does not list. A file from an <see cref="IPlaintextBundleSource"/> that holds decrypted
/// personal data gets <c>plaintextPersonal: true</c> in its manifest entry.
/// NOT IMPLEMENTED (doc 44 is silent or the slice is later): parts of at most 4 GiB, resumable chunks, the self-check of
/// doc 44 section 6.2, and a scope field in the manifest.
/// </remarks>
public static class ExportBundleAssembler
{
    private static readonly JsonSerializerOptions _manifestOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The directory name of doc 44 section 5: <c>bazaar-export-&lt;instance-id&gt;-&lt;utc-timestamp&gt;</c>. The timestamp shape
    /// <c>yyyyMMddTHHmmssZ</c> is a choice the doc leaves open.
    /// </summary>
    public static string DirectoryName(string instanceId, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return "bazaar-export-" + instanceId + "-"
            + createdAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Assembles a signed bundle. Throws on any failure and leaves nothing at <see cref="BundleAssemblyOptions.OutputDirectory"/>.</summary>
    public static async Task<BundleAssemblyResult> AssembleAsync(
        IEnumerable<IBundleSource> sources,
        BundleAssemblyOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        ArgumentNullException.ThrowIfNull(options.SigningKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BufferSize, 1);
        if (options.EncryptedArchivePath is not null && string.IsNullOrEmpty(options.Passphrase))
        {
            throw new ArgumentException("A passphrase is required when an encrypted archive path is set.", nameof(options));
        }

        var ordered = sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Id == ordered[i - 1].Id)
            {
                throw new ArgumentException("Two sources share the id " + ordered[i].Id, nameof(sources));
            }
        }

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputDirectory));
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException("Refusing to write: directory is not empty: " + target);
        }

        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Output directory has no parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, "." + Path.GetFileName(target) + ".partial-" + Guid.NewGuid().ToString("N"));
        string? archiveTemp = null;
        try
        {
            Directory.CreateDirectory(work);
            var sink = new HashingDirectorySink(work, options.BufferSize);
            var plaintext = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var source in ordered)
            {
                ct.ThrowIfCancellationRequested();
                await source.ExportAsync(sink, ct);
                if (source is IPlaintextBundleSource flagging)
                {
                    plaintext.UnionWith(flagging.PlaintextPersonalPaths);
                }
            }

            var contentFiles = sink.Complete();
            var listed = contentFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in plaintext.Where(p => !listed.Contains(p)))
            {
                throw new InvalidOperationException("A source flagged a plaintext file it did not write: " + path);
            }

            await sink.WriteGeneratedAsync(
                BundleReadme.FileName,
                BundleReadme.Build(options.InstanceId, options.CreatedAt, [.. listed], plaintext),
                ct);
            var files = sink.Complete();
            var manifest = BuildManifest(options, files, plaintext);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest);
            await File.WriteAllBytesAsync(Path.Combine(work, ExportBundleWriter.ManifestFileName), manifestBytes, ct);
            var (signature, publicKey) = BundleSigner.SignManifest(manifestBytes, options.SigningKey);
            await File.WriteAllBytesAsync(Path.Combine(work, BundleSigner.SignatureFileName), signature, ct);
            await File.WriteAllBytesAsync(Path.Combine(work, BundleSigner.PublicKeyFileName), publicKey, ct);

            // Doc 44 section 6.2: re-read what was written before the bundle is offered. A failure leaves no bundle.
            var selfCheck = await SelfCheckAsync(work, manifestBytes, files, signature, publicKey, ct);
            if (selfCheck.Count > 0)
            {
                throw new InvalidOperationException("The bundle failed its self-check and is incomplete: " + string.Join(" ", selfCheck));
            }

            string? archive = null;
            if (options.EncryptedArchivePath is not null)
            {
                archive = Path.GetFullPath(options.EncryptedArchivePath);
                if (File.Exists(archive) || Directory.Exists(archive))
                {
                    throw new InvalidOperationException("Refusing to overwrite: " + archive);
                }

                var bundle = await ReadDirectoryAsync(work, ct);
                archiveTemp = archive + ".partial-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                await File.WriteAllBytesAsync(
                    archiveTemp,
                    ExportBundleCipher.Encrypt(bundle, options.Passphrase!, options.KdfParameters),
                    ct);
            }

            if (Directory.Exists(target)) Directory.Delete(target);
            Directory.Move(work, target);
            if (archiveTemp is not null)
            {
                File.Move(archiveTemp, archive!);
                archiveTemp = null;
            }

            return new BundleAssemblyResult(target, manifest, files, archive);
        }
        catch
        {
            TryDelete(work);
            if (archiveTemp is not null && File.Exists(archiveTemp)) File.Delete(archiveTemp);
            throw;
        }
    }

    /// <summary>
    /// The section 6.2 self-check at the file level, streaming from disk: the manifest is the bytes that were signed and its
    /// signature verifies, every listed file hashes to its entry, and no file on disk is unlisted. Chain checks (GC, audit) belong to
    /// <c>Bazaar.Server.Portability.Verifier</c>, which references this project, so the sources run their own chain self-checks
    /// (<see cref="GcExportSelfCheck"/>, the audit replay) and the assembler checks the bytes. Returns the problems, empty when sound.
    /// </summary>
    private static async Task<List<string>> SelfCheckAsync(
        string work, byte[] manifestBytes, IReadOnlyList<ExportedFile> files, byte[] signature, byte[] publicKey, CancellationToken ct)
    {
        var problems = new List<string>();
        var manifestOnDisk = await File.ReadAllBytesAsync(Path.Combine(work, ExportBundleWriter.ManifestFileName), ct);
        if (!manifestOnDisk.AsSpan().SequenceEqual(manifestBytes))
        {
            problems.Add("manifest.json on disk is not the manifest that was signed.");
        }

        var signatureOnDisk = await File.ReadAllBytesAsync(Path.Combine(work, BundleSigner.SignatureFileName), ct);
        var keyOnDisk = await File.ReadAllBytesAsync(Path.Combine(work, BundleSigner.PublicKeyFileName), ct);
        if (!signatureOnDisk.AsSpan().SequenceEqual(signature) || !keyOnDisk.AsSpan().SequenceEqual(publicKey)
            || !BundleSigner.VerifyManifest(manifestOnDisk, signatureOnDisk, keyOnDisk))
        {
            problems.Add("the manifest signature does not verify.");
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            listed.Add(file.Path);
            var full = Path.Combine(work, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                problems.Add("missing from the bundle: " + file.Path);
                continue;
            }

            await using var stream = File.OpenRead(full);
            var actual = await GcJsonlFormat.HashAsync(file.Path, stream, ct);
            if (actual.Bytes != file.Bytes || actual.Sha256 != file.Sha256)
            {
                problems.Add("changed after it was hashed: " + file.Path);
            }
        }

        foreach (var full in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(work, full).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != ExportBundleWriter.ManifestFileName && !BundleSigner.IsSignatureFile(relative) && !listed.Contains(relative))
            {
                problems.Add("not listed in the manifest: " + relative);
            }
        }

        return problems;
    }

    /// <summary>Reads a bundle directory (as <see cref="AssembleAsync"/> wrote it) into memory, for verification or encryption.</summary>
    public static async Task<ExportBundle> ReadDirectoryAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var files = new List<BundleFile>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new BundleFile(relative, await File.ReadAllBytesAsync(full, ct)));
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        var manifest = files.FirstOrDefault(f => f.Path == ExportBundleWriter.ManifestFileName)
            ?? throw new InvalidOperationException("manifest.json is missing from " + root);
        return new ExportBundle(Encoding.UTF8.GetString(manifest.Content), files);
    }

    private static string BuildManifest(BundleAssemblyOptions options, IReadOnlyList<ExportedFile> files, SortedSet<string> plaintext)
    {
        var entries = files
            .Select(f => new ExportBundleWriter.ManifestEntry(f.Path, f.Sha256, f.Bytes, plaintext.Contains(f.Path) ? true : null))
            .ToList();
        var document = new ExportBundleWriter.ManifestDocument(
            ExportBundleWriter.SchemaVersion,
            options.InstanceId,
            options.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            entries.Count,
            entries.Sum(e => e.Bytes),
            entries);
        return JsonSerializer.Serialize(document, _manifestOptions);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the original exception is the one that matters.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>An <see cref="IExportFileSink"/> that writes into a directory and hashes each file as it streams.</summary>
    private sealed class HashingDirectorySink(string root, int bufferSize) : IExportFileSink
    {
        private readonly Dictionary<string, ExportedFile?> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _prefix = root + Path.DirectorySeparatorChar;

        public async Task<Stream> OpenAsync(string path, CancellationToken ct)
        {
            ValidatePath(path);
            return await OpenCoreAsync(path, ct);
        }

        private Task<Stream> OpenCoreAsync(string path, CancellationToken ct)
        {
            if (!_files.TryAdd(path, null))
            {
                throw new InvalidOperationException("Path written twice (case-insensitive): " + path);
            }

            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(_prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Bundle path escapes the target directory: " + path);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
            return Task.FromResult<Stream>(new HashingStream(file, (sha, bytes) => _files[path] = new ExportedFile(path, sha, bytes)));
        }

        /// <summary>Writes a file the assembler generated. Sources cannot use these names (see <see cref="ValidatePath"/>).</summary>
        public async Task WriteGeneratedAsync(string path, byte[] content, CancellationToken ct)
        {
            await using var stream = await OpenCoreAsync(path, ct);
            await stream.WriteAsync(content, ct);
        }

        public List<ExportedFile> Complete()
        {
            var open = _files.Where(kv => kv.Value is null).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            if (open.Count > 0)
            {
                throw new InvalidOperationException("Exporter left a file open: " + string.Join(", ", open));
            }

            return _files.Values.Select(v => v!).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Bundle path is empty.");
            if (path.Contains('\\', StringComparison.Ordinal)) throw new ArgumentException("Bundle path has a backslash: " + path);
            if (path.StartsWith('/') || Path.IsPathRooted(path)) throw new ArgumentException("Bundle path is absolute: " + path);
            if (path.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("Bundle path has a drive or scheme: " + path);
            if (path == ExportBundleWriter.ManifestFileName) throw new ArgumentException("Bundle path is reserved for the manifest: " + path);
            if (BundleSigner.IsSignatureFile(path)) throw new ArgumentException("Bundle path is reserved for a signature file: " + path);
            if (string.Equals(path, BundleReadme.FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Bundle path is reserved for the generated README: " + path);
            }

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0) throw new ArgumentException("Bundle path has an empty segment: " + path);
                if (segment is "." or "..") throw new ArgumentException("Bundle path has a traversal segment: " + path);
            }
        }
    }

    /// <summary>Write-only stream that forwards to a file while feeding an incremental SHA-256, and reports hash and size on dispose.</summary>
    private sealed class HashingStream(FileStream inner, Action<string, long> completed) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;
        private bool _done;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytes;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _hash.AppendData(buffer);
            _bytes += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            _hash.AppendData(buffer.Span);
            _bytes += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                inner.Dispose();
                Finish();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                _done = true;
                await inner.DisposeAsync();
                Finish();
            }

            await base.DisposeAsync();
        }

        private void Finish()
        {
            completed(Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(), _bytes);
            _hash.Dispose();
        }
    }
}
