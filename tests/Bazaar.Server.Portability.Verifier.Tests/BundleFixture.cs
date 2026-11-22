using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Verifier.Tests;

/// <summary>A seeded audit chain with signed checkpoints, built the way the server builds them (hash rules from AuditHashing).</summary>
internal sealed class AuditSeed : IAuditLogReadSource, IAuditSignatureVerifier
{
    private static readonly DateTimeOffset _start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key;

    public AuditSeed(ECDsa key, int count, int perCheckpoint)
    {
        _key = key;
        var prev = AuditHashing.GenesisHash();
        long first = 1;
        for (var i = 1; i <= count; i++)
        {
            var ts = _start.AddMinutes(i);
            var payload = "{\"n\":" + i + "}";
            var hash = AuditHashing.ComputeRowHash(i, ts, "actor" + i, "kind.test", payload, prev);
            Rows.Add(new AuditRow(i, ts, "actor" + i, "kind.test", payload, prev, hash));
            prev = hash;
            if (i % perCheckpoint == 0)
            {
                AddCheckpoint(first, i);
                first = i + 1;
            }
        }
    }

    public List<AuditRow> Rows { get; } = [];

    public List<AuditCheckpointRecord> Checkpoints { get; } = [];

    private void AddCheckpoint(long first, long last)
    {
        var segment = Rows.Where(r => r.Seq >= first && r.Seq <= last).Select(r => r.Hash).ToList();
        var createdAt = _start.AddDays(1).AddMinutes(last);
        var head = Rows.First(r => r.Seq == last).Hash;
        var root = AuditMerkle.ComputeRoot(segment);
        var signature = _key.SignData(AuditHashing.CheckpointSignedBytes(first, last, last - first + 1, head, root, createdAt), HashAlgorithmName.SHA256);
        Checkpoints.Add(new AuditCheckpointRecord(first, last, last - first + 1, head, root, createdAt, "audit-key-1", signature));
    }

    public bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature) => _key.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256);

    public async IAsyncEnumerable<AuditCheckpointRecord> ReadCheckpointsAsync(long? fromLastSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var checkpoint in Checkpoints.Where(c => fromLastSeq is null || c.LastSeq >= fromLastSeq))
        {
            yield return checkpoint;
        }
    }

    public Task<AuditCheckpointRecord?> ReadCheckpointCoveringAsync(long seq, CancellationToken ct) =>
        Task.FromResult(Checkpoints.FirstOrDefault(c => seq >= c.FirstSeq && seq <= c.LastSeq));

    public async IAsyncEnumerable<AuditRow> ReadRowsAsync(long fromSeq, long? toSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var row in Rows.Where(r => r.Seq >= fromSeq && (toSeq is null || r.Seq <= toSeq)))
        {
            yield return row;
        }
    }
}

/// <summary>
/// A small bundle written to disk with the real writer and signer: two GCs and an audit archive with proofs. Each test builds
/// its own, in a fresh temp directory, then tampers with files or rebuilds with a changed file list.
/// </summary>
internal sealed class BundleFixture : IDisposable
{
    public BundleFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("bazaar-verify-").FullName;
        ExportKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AuditKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Audit = new AuditSeed(AuditKey, count: 7, perCheckpoint: 3);
    }

    public string Directory { get; }

    public ECDsa ExportKey { get; }

    public ECDsa AuditKey { get; }

    public AuditSeed Audit { get; }

    public byte[] ExportPublicKey => ExportKey.ExportSubjectPublicKeyInfo();

    public byte[] AuditPublicKey => AuditKey.ExportSubjectPublicKeyInfo();

    public VerifyOptions Options => new(AuditKey: AuditPublicKey);

    /// <summary>The content files of the untouched bundle (everything but the manifest and signature files).</summary>
    public List<BundleFile> BaseFiles()
    {
        var files = new List<BundleFile>();
        files.AddRange(Gc("g1", 4));
        files.AddRange(Gc("g2", 2));
        files.Add(new BundleFile("config/settings.json", Encoding.UTF8.GetBytes("{\"a\":1}\n")));
        var export = new AuditExporter(Audit, Audit).ExportAsync(null).GetAwaiter().GetResult();
        files.AddRange(export.Files);
        var proofs = new AuditProofExporter(Audit, Audit);
        files.Add(proofs.ExportAsync(2).GetAwaiter().GetResult());
        files.Add(proofs.ExportAsync(6).GetAwaiter().GetResult());
        return files;
    }

    /// <summary>Writes a signed bundle of <paramref name="files"/> into the directory (which must be empty).</summary>
    public void Write(IReadOnlyList<BundleFile> files, ECDsa? signer = null)
    {
        var bundle = BundleSigner.Sign(ExportBundleWriter.Build("inst-1", new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), files), signer ?? ExportKey);
        ExportBundleWriter.WriteTo(bundle, Directory);
    }

    public void WriteUntouched() => Write(BaseFiles());

    /// <summary>Rebuilds a consistent, correctly signed bundle after changing one content file (so only the chain checks can catch it).</summary>
    public void WriteWith(Func<BundleFile, BundleFile?> change)
    {
        var files = BaseFiles().Select(change).Where(f => f is not null).Select(f => f!).ToList();
        Write(files);
    }

    public string PathOf(string relative) => System.IO.Path.Combine(Directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public VerifyReport Run(VerifyOptions? options = null) => BundleVerifier.Verify(Directory, options ?? Options);

    public void Dispose()
    {
        ExportKey.Dispose();
        AuditKey.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IEnumerable<BundleFile> Gc(string id, int events)
    {
        var lines = new StringBuilder();
        string? prev = null;
        for (var i = 1; i <= events; i++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + i + ":" + prev))).ToLowerInvariant();
            var prevJson = prev is null ? "null" : "\"" + prev + "\"";
            lines.Append("{\"seq\":").Append(i).Append(",\"kind\":\"message\",\"prev_event_hash\":").Append(prevJson)
                .Append(",\"event_hash\":\"").Append(hash).Append("\"}\n");
            prev = hash;
        }

        yield return new BundleFile($"gc/{id}/events.jsonl", Encoding.UTF8.GetBytes(lines.ToString()));
        yield return new BundleFile($"gc/{id}/messages.jsonl", Encoding.UTF8.GetBytes("{\"id\":1}\n{\"id\":2}\n"));
        yield return new BundleFile(
            $"gc/{id}/head.json",
            Encoding.UTF8.GetBytes("{\"last_seq\":" + events + ",\"head_hash\":\"" + prev + "\",\"counts\":{\"events\":" + events + ",\"messages\":2}}\n"));
    }
}
