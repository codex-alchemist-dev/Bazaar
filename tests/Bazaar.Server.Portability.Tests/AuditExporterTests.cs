using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Tests;

/// <summary>A chain, its checkpoints and a signer, built the way the server builds them (hash rules mirrored in AuditHashing).</summary>
internal sealed class AuditChainFixture : IAuditLogReadSource, IAuditSignatureVerifier
{
    private static readonly DateTimeOffset _start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public List<AuditRow> Rows { get; } = [];

    public List<AuditCheckpointRecord> Checkpoints { get; } = [];

    /// <summary>Builds <paramref name="count"/> rows and a checkpoint after every <paramref name="perCheckpoint"/> rows.</summary>
    public static AuditChainFixture Build(int count, int perCheckpoint)
    {
        var fixture = new AuditChainFixture();
        var prev = AuditHashing.GenesisHash();
        long firstOfSegment = 1;
        for (var i = 1; i <= count; i++)
        {
            var ts = _start.AddMinutes(i);
            var payload = i % 3 == 0 ? "{\"name\":\"enc:v1:AAECAwQ=\"}" : "{\"n\":" + i + "}";
            var hash = AuditHashing.ComputeRowHash(i, ts, "actor" + i, "kind.test", payload, prev);
            fixture.Rows.Add(new AuditRow(i, ts, "actor" + i, "kind.test", payload, prev, hash));
            prev = hash;
            if (i % perCheckpoint == 0)
            {
                fixture.AddCheckpoint(firstOfSegment, i);
                firstOfSegment = i + 1;
            }
        }

        return fixture;
    }

    public void AddCheckpoint(long first, long last)
    {
        var segment = Rows.Where(r => r.Seq >= first && r.Seq <= last).Select(r => r.Hash).ToList();
        var createdAt = _start.AddDays(1).AddMinutes(last);
        var head = Rows.First(r => r.Seq == last).Hash;
        var root = AuditMerkle.ComputeRoot(segment);
        var signature = _key.SignData(AuditHashing.CheckpointSignedBytes(first, last, last - first + 1, head, root, createdAt), HashAlgorithmName.SHA256);
        Checkpoints.Add(new AuditCheckpointRecord(first, last, last - first + 1, head, root, createdAt, "audit-key-1", signature));
    }

    public bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature) =>
        _key.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256);

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

    public static AuditRow With(AuditRow row, string? payload = null, byte[]? prevHash = null, byte[]? hash = null) =>
        row with { Payload = payload ?? row.Payload, PrevHash = prevHash ?? row.PrevHash, Hash = hash ?? row.Hash };
}

public class AuditHashingVectorTests
{
    // Vectors produced by running the server's AuditChain.ComputeHash, MerkleTree and AuditCheckpoint.SignedBytes
    // (src/Bazaar.Server/Security/Audit) on these inputs. They pin the mirrored rules; they are not self-generated.
    private static byte[] Leaf(int n) => SHA256.HashData(BitConverter.GetBytes(n));

    [Fact]
    public void Row_hash_matches_the_server_vector()
    {
        var ts = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        var hash = AuditHashing.ComputeRowHash(7, ts, "alice", "auth.signin", "{\"k\":\"v\"}", AuditHashing.GenesisHash());

        Assert.Equal("5286D2A58D1D84D39F552321B6EB257A1B7B04428F567734BA6F753A6B2F9029", Convert.ToHexString(hash));
    }

    [Theory]
    [InlineData(1, "965053F8E65891F8079863A0808B66D6DBBA6E4AC34B710F797019ED633ADF92")]
    [InlineData(2, "E92E9CAEDEB87A8C24CF1164FE9B28EE226C68888D34583A35FF7F714748B45F")]
    [InlineData(3, "39EC9055D22105207AACEC2CC76DC59060DA9383C8704183312C9A53558528E6")]
    [InlineData(5, "4D2BBED57F5949A0276238F2211AA0EFAC3B56065E4BF5E53FBAEACF47C0A663")]
    [InlineData(6, "1FB764705456EB779693BCE63AE380D6A0F1CF1DEB254825E104C2261CB0C49E")]
    public void Merkle_root_matches_the_server_vector(int leaves, string expectedHex)
    {
        var root = AuditMerkle.ComputeRoot(Enumerable.Range(0, leaves).Select(Leaf).ToList());

        Assert.Equal(expectedHex, Convert.ToHexString(root));
    }

    [Fact]
    public void Empty_merkle_root_is_sha256_of_nothing()
    {
        Assert.Equal(SHA256.HashData(ReadOnlySpan<byte>.Empty), AuditMerkle.ComputeRoot([]));
    }

    [Fact]
    public void Checkpoint_signed_bytes_match_the_server_vector()
    {
        var ts = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        var bytes = AuditHashing.CheckpointSignedBytes(1, 3, 3, Leaf(100), Leaf(101), ts);

        Assert.Equal(
            "0000001A62617A6161722E61756469742E636865636B706F696E742E763100000000000000010000000000000003"
            + "00000000000000030000002040E736C02A102A050E1555781B4171020A4279ADAA7ED9CA3CC9633A0ADE9C37"
            + "000000208FC6CB4A9816D75621652DE783118EEE13112CDA196E61D1BDE0460F29B26E3E08DF328F7EDCA000",
            Convert.ToHexString(bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    public void Every_leaf_path_recomputes_the_root(int leaves)
    {
        var hashes = Enumerable.Range(0, leaves).Select(Leaf).ToList();
        var root = AuditMerkle.ComputeRoot(hashes);

        for (var i = 0; i < leaves; i++)
        {
            var path = AuditMerkle.BuildPath(hashes, i);
            Assert.Equal(root, AuditMerkle.ComputeRootFromPath(hashes[i], i, leaves, path));
        }
    }
}

public class AuditChainReplayTests
{
    [Fact]
    public void Good_chain_from_genesis_verifies_and_counts_the_tail()
    {
        var chain = AuditChainFixture.Build(10, 4);

        var result = AuditChainReplay.Replay(chain.Rows, chain.Checkpoints, chain, startsAtGenesis: true);

        Assert.True(result.IsValid);
        Assert.Equal(10, result.RowsChecked);
        Assert.Equal(2, result.CheckpointsChecked);
        Assert.Equal(2, result.UncheckpointedTailRows);
    }

    [Fact]
    public void Good_chain_from_a_middle_checkpoint_verifies()
    {
        var chain = AuditChainFixture.Build(12, 4);

        var result = AuditChainReplay.Replay(chain.Rows.Where(r => r.Seq > 4), chain.Checkpoints.Where(c => c.LastSeq >= 4).ToList(), chain, startsAtGenesis: false);

        Assert.True(result.IsValid);
        Assert.Equal(8, result.RowsChecked);
        Assert.Equal(3, result.CheckpointsChecked);
    }

    [Fact]
    public void Tampered_row_reports_that_row_first()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var rows = chain.Rows.Select(r => r.Seq == 3 ? AuditChainFixture.With(r, payload: "{\"n\":999}") : r).ToList();

        var result = AuditChainReplay.Replay(rows, chain.Checkpoints, chain, true);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.HashMismatch, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Tampered_row_with_recomputed_hash_breaks_the_next_link()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var forged = chain.Rows[2] with { Payload = "{\"n\":999}" };
        forged = forged with { Hash = AuditHashing.ComputeRowHash(forged) };
        var rows = chain.Rows.ToList();
        rows[2] = forged;

        var result = AuditChainReplay.Replay(rows, chain.Checkpoints, chain, true);

        Assert.Equal(4, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.PrevHashMismatch, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Dropped_row_reports_the_row_after_the_gap()
    {
        var chain = AuditChainFixture.Build(8, 4);

        var result = AuditChainReplay.Replay(chain.Rows.Where(r => r.Seq != 3), chain.Checkpoints, chain, true);

        Assert.Equal(4, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.SequenceBroken, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Reordered_rows_report_the_first_out_of_place_row()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var rows = chain.Rows.ToList();
        (rows[4], rows[5]) = (rows[5], rows[4]);

        var result = AuditChainReplay.Replay(rows, chain.Checkpoints, chain, true);

        Assert.Equal(6, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.SequenceBroken, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Wrong_chain_head_in_a_checkpoint_is_reported()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var checkpoints = chain.Checkpoints.ToList();
        checkpoints[1] = checkpoints[1] with { ChainHeadHash = chain.Rows[0].Hash };

        var result = AuditChainReplay.Replay(chain.Rows, checkpoints, chain, true);

        Assert.Equal(8, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.CheckpointHeadMismatch, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Wrong_merkle_root_in_a_checkpoint_is_reported()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var checkpoints = chain.Checkpoints.ToList();
        checkpoints[0] = checkpoints[0] with { MerkleRoot = SHA256.HashData([1]) };

        var result = AuditChainReplay.Replay(chain.Rows, checkpoints, chain, true);

        Assert.Equal(4, result.FirstMismatch!.Seq);
        Assert.Equal(AuditReplayFailureKind.CheckpointMerkleMismatch, result.FirstMismatch.Kind);
    }

    [Fact]
    public void Forged_signature_is_reported()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var checkpoints = chain.Checkpoints.ToList();
        var bad = (byte[])checkpoints[0].Signature.Clone();
        bad[0] ^= 1;
        checkpoints[0] = checkpoints[0] with { Signature = bad };

        var result = AuditChainReplay.Replay(chain.Rows, checkpoints, chain, true);

        Assert.Equal(AuditReplayFailureKind.CheckpointSignatureInvalid, result.FirstMismatch!.Kind);
    }

    [Fact]
    public void Tampered_starting_checkpoint_is_reported_before_any_row()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var checkpoints = chain.Checkpoints.ToList();
        checkpoints[0] = checkpoints[0] with { ChainHeadHash = SHA256.HashData([9]) };

        var result = AuditChainReplay.Replay(chain.Rows.Where(r => r.Seq > 4), checkpoints, chain, startsAtGenesis: false);

        Assert.Equal(AuditReplayFailureKind.CheckpointSignatureInvalid, result.FirstMismatch!.Kind);
        Assert.Equal(0, result.RowsChecked);
    }

    [Fact]
    public void Rows_cut_short_of_a_checkpoint_fail()
    {
        var chain = AuditChainFixture.Build(8, 4);

        var result = AuditChainReplay.Replay(chain.Rows.Where(r => r.Seq <= 6), chain.Checkpoints, chain, true);

        Assert.Equal(AuditReplayFailureKind.Truncated, result.FirstMismatch!.Kind);
        Assert.Equal(8, result.FirstMismatch.Seq);
    }

    [Fact]
    public void Gap_between_checkpoints_fails()
    {
        var chain = AuditChainFixture.Build(12, 4);
        var checkpoints = chain.Checkpoints.Where(c => c.LastSeq != 8).ToList();

        var result = AuditChainReplay.Replay(chain.Rows, checkpoints, chain, true);

        Assert.False(result.IsValid);
        Assert.Equal(AuditReplayFailureKind.CheckpointRangeBroken, result.FirstMismatch!.Kind);
    }

    [Fact]
    public void Missing_start_checkpoint_fails()
    {
        var chain = AuditChainFixture.Build(4, 4);

        Assert.False(AuditChainReplay.Replay(chain.Rows, [], chain, startsAtGenesis: false).IsValid);
    }
}

public class AuditExporterTests
{
    private static readonly string[] _expectedPaths =
        ["audit/archive.json", "audit/checkpoints.jsonl", "audit/entries.jsonl"];

    [Fact]
    public async Task Export_from_genesis_verifies_offline_and_matches_the_bundle_layout()
    {
        var chain = AuditChainFixture.Build(10, 4);

        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.SelfCheck.IsValid);
        Assert.Equal(_expectedPaths, result.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        var again = AuditArchiveVerifier.VerifyFiles(
            File(result, AuditArchiveFormat.EntriesPath), File(result, AuditArchiveFormat.CheckpointsPath), File(result, AuditArchiveFormat.ArchivePath), chain);
        Assert.True(again.IsValid);
        var bundle = ExportBundleWriter.Build("inst", DateTimeOffset.UnixEpoch, result.Files);
        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.Contains("audit/archive.json", bundle.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_from_a_checkpoint_starts_after_it_and_keeps_it_first()
    {
        var chain = AuditChainFixture.Build(12, 4);

        var result = await new AuditExporter(chain, chain).ExportAsync(4, TestContext.Current.CancellationToken);

        Assert.True(result.SelfCheck.IsValid);
        var rows = AuditArchiveFormat.TryReadRows(File(result, AuditArchiveFormat.EntriesPath))!;
        var checkpoints = AuditArchiveFormat.TryReadCheckpoints(File(result, AuditArchiveFormat.CheckpointsPath))!;
        Assert.Equal(5, rows[0].Seq);
        Assert.Equal(12, rows[^1].Seq);
        Assert.Equal(4, checkpoints[0].LastSeq);
        Assert.True(AuditArchiveVerifier.VerifyFiles(
            File(result, AuditArchiveFormat.EntriesPath), File(result, AuditArchiveFormat.CheckpointsPath), File(result, AuditArchiveFormat.ArchivePath), chain).IsValid);
    }

    [Fact]
    public async Task Archive_marker_says_it_is_not_part_of_the_live_chain()
    {
        var chain = AuditChainFixture.Build(8, 4);

        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);

        var marker = AuditArchiveFormat.TryReadArchive(File(result, AuditArchiveFormat.ArchivePath))!;
        Assert.False(marker.PartOfLiveChain);
        Assert.Contains("Never merged into the live chain", marker.ImportRule, StringComparison.Ordinal);
        var text = Encoding.UTF8.GetString(File(result, AuditArchiveFormat.ArchivePath));
        Assert.Contains("\"part_of_live_chain\": false", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_claiming_the_live_chain_fails_verification()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(File(result, AuditArchiveFormat.ArchivePath)).Replace("\"part_of_live_chain\": false", "\"part_of_live_chain\": true", StringComparison.Ordinal);

        var check = AuditArchiveVerifier.VerifyFiles(
            File(result, AuditArchiveFormat.EntriesPath), File(result, AuditArchiveFormat.CheckpointsPath), Encoding.UTF8.GetBytes(text), chain);

        Assert.False(check.IsValid);
    }

    [Fact]
    public async Task Checkpoint_signatures_are_exported_untouched()
    {
        var chain = AuditChainFixture.Build(8, 4);

        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);

        var exported = AuditArchiveFormat.TryReadCheckpoints(File(result, AuditArchiveFormat.CheckpointsPath))!;
        Assert.Equal(chain.Checkpoints.Count, exported.Count);
        for (var i = 0; i < exported.Count; i++)
        {
            Assert.Equal(chain.Checkpoints[i].Signature, exported[i].Signature);
            Assert.Equal(chain.Checkpoints[i].ChainHeadHash, exported[i].ChainHeadHash);
            Assert.Equal(chain.Checkpoints[i].MerkleRoot, exported[i].MerkleRoot);
            Assert.Equal(chain.Checkpoints[i].KeyId, exported[i].KeyId);
        }
    }

    [Fact]
    public async Task Encrypted_fields_stay_ciphertext()
    {
        var chain = AuditChainFixture.Build(6, 3);

        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);

        var rows = AuditArchiveFormat.TryReadRows(File(result, AuditArchiveFormat.EntriesPath))!;
        Assert.Equal(chain.Rows.Select(r => r.Payload), rows.Select(r => r.Payload));
        Assert.Contains("enc:v1:AAECAwQ=", rows[2].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_is_deterministic()
    {
        var chain = AuditChainFixture.Build(9, 4);
        var exporter = new AuditExporter(chain, chain);

        var first = await exporter.ExportAsync(null, TestContext.Current.CancellationToken);
        var second = await exporter.ExportAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(first.Files.Select(f => f.Path), second.Files.Select(f => f.Path));
        for (var i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].Content, second.Files[i].Content);
        }
    }

    [Fact]
    public async Task Export_fails_closed_on_a_tampered_row()
    {
        var chain = AuditChainFixture.Build(8, 4);
        chain.Rows[5] = AuditChainFixture.With(chain.Rows[5], payload: "{\"n\":999}");

        var ex = await Assert.ThrowsAsync<AuditExportException>(() => new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("seq 6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_fails_closed_on_a_bad_signature()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var bad = (byte[])chain.Checkpoints[1].Signature.Clone();
        bad[3] ^= 1;
        chain.Checkpoints[1] = chain.Checkpoints[1] with { Signature = bad };

        await Assert.ThrowsAsync<AuditExportException>(() => new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_fails_closed_on_an_unknown_starting_checkpoint()
    {
        var chain = AuditChainFixture.Build(8, 4);

        await Assert.ThrowsAsync<AuditExportException>(() => new AuditExporter(chain, chain).ExportAsync(5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_of_an_empty_log_is_valid()
    {
        var chain = AuditChainFixture.Build(0, 4);

        var result = await new AuditExporter(chain, chain).ExportAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.SelfCheck.IsValid);
        Assert.Equal(0, result.SelfCheck.RowsChecked);
    }

    [Fact]
    public void Malformed_archive_files_fail_without_throwing()
    {
        var chain = AuditChainFixture.Build(4, 4);

        var result = AuditArchiveVerifier.VerifyFiles("not json\n"u8, "{}\n"u8, true, chain);

        Assert.False(result.IsValid);
        Assert.Equal(AuditReplayFailureKind.MalformedInput, result.FirstMismatch!.Kind);
    }

    private static byte[] File(AuditExportResult result, string path) => result.Files.Single(f => f.Path == path).Content;
}

public class AuditProofTests
{
    [Fact]
    public async Task Proof_for_every_row_verifies_offline()
    {
        var chain = AuditChainFixture.Build(11, 5);
        var exporter = new AuditProofExporter(chain, chain);

        for (var seq = 1; seq <= 10; seq++)
        {
            var file = await exporter.ExportAsync(seq, TestContext.Current.CancellationToken);

            Assert.True(AuditInclusionProofVerifier.Verify(file.Content, chain).IsValid);
            Assert.StartsWith("audit/proofs/", file.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Proof_names_the_checkpoint_and_carries_leaf_path_and_root()
    {
        var chain = AuditChainFixture.Build(8, 4);

        var file = await new AuditProofExporter(chain, chain).ExportAsync(6, TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("\"checkpoint_id\": \"5-8\"", text, StringComparison.Ordinal);
        Assert.Contains("\"leaf_hash\"", text, StringComparison.Ordinal);
        Assert.Contains("\"siblings\"", text, StringComparison.Ordinal);
        Assert.Contains("\"root\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_sibling_fails()
    {
        var chain = AuditChainFixture.Build(8, 8);
        var file = await new AuditProofExporter(chain, chain).ExportAsync(3, TestContext.Current.CancellationToken);
        var proof = System.Text.Json.JsonSerializer.Deserialize(file.Content, AuditJson.Default.InclusionProofFile)!;
        var siblings = proof.Siblings!.Select(s => (byte[])s.Clone()).ToList();
        siblings[0][0] ^= 1;

        var result = AuditInclusionProofVerifier.Verify(proof with { Siblings = siblings }, chain);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Extra_or_missing_sibling_fails()
    {
        var chain = AuditChainFixture.Build(8, 8);
        var proof = System.Text.Json.JsonSerializer.Deserialize((await new AuditProofExporter(chain, chain).ExportAsync(3, TestContext.Current.CancellationToken)).Content, AuditJson.Default.InclusionProofFile)!;

        Assert.False(AuditInclusionProofVerifier.Verify(proof with { Siblings = proof.Siblings!.Take(1).ToList() }, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(proof with { Siblings = proof.Siblings!.Append(proof.Siblings![0]).ToList() }, chain).IsValid);
    }

    [Fact]
    public async Task Wrong_leaf_root_or_signature_fails()
    {
        var chain = AuditChainFixture.Build(8, 8);
        var proof = System.Text.Json.JsonSerializer.Deserialize((await new AuditProofExporter(chain, chain).ExportAsync(3, TestContext.Current.CancellationToken)).Content, AuditJson.Default.InclusionProofFile)!;
        var other = chain.Rows[4].Hash;
        var badSignature = (byte[])proof.Checkpoint!.Signature!.Clone();
        badSignature[0] ^= 1;

        Assert.False(AuditInclusionProofVerifier.Verify(proof with { LeafHash = other }, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(proof with { Root = other }, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(proof with { Checkpoint = proof.Checkpoint with { Signature = badSignature } }, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(proof with { CheckpointId = "1-9" }, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(proof with { LeafIndex = 4 }, chain).IsValid);
    }

    [Fact]
    public async Task Anchored_root_must_match_when_given()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var file = await new AuditProofExporter(chain, chain).ExportAsync(2, TestContext.Current.CancellationToken);

        Assert.True(AuditInclusionProofVerifier.Verify(file.Content, chain, chain.Checkpoints[0].MerkleRoot).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify(file.Content, chain, chain.Checkpoints[1].MerkleRoot).IsValid);
    }

    [Fact]
    public async Task Row_check_binds_the_proof_to_the_row_fields()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var file = await new AuditProofExporter(chain, chain).ExportAsync(2, TestContext.Current.CancellationToken);

        Assert.True(AuditInclusionProofVerifier.VerifyRow(file.Content, chain.Rows[1], chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.VerifyRow(file.Content, chain.Rows[2], chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.VerifyRow(file.Content, AuditChainFixture.With(chain.Rows[1], payload: "{}"), chain).IsValid);
    }

    [Fact]
    public async Task Hostile_input_is_invalid_not_an_exception()
    {
        var chain = AuditChainFixture.Build(4, 4);

        Assert.False(AuditInclusionProofVerifier.Verify("garbage"u8, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify("{}"u8, chain).IsValid);
        Assert.False(AuditInclusionProofVerifier.Verify("null"u8, chain).IsValid);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Row_without_a_checkpoint_gets_no_proof()
    {
        var chain = AuditChainFixture.Build(6, 4);

        await Assert.ThrowsAsync<AuditExportException>(() => new AuditProofExporter(chain, chain).ExportAsync(6, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Proof_export_fails_closed_when_rows_do_not_match_the_checkpoint()
    {
        var chain = AuditChainFixture.Build(8, 8);
        chain.Rows[2] = AuditChainFixture.With(chain.Rows[2], payload: "{\"n\":1}");

        await Assert.ThrowsAsync<AuditExportException>(() => new AuditProofExporter(chain, chain).ExportAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Proof_export_is_deterministic()
    {
        var chain = AuditChainFixture.Build(8, 4);
        var exporter = new AuditProofExporter(chain, chain);

        Assert.Equal((await exporter.ExportAsync(3, TestContext.Current.CancellationToken)).Content, (await exporter.ExportAsync(3, TestContext.Current.CancellationToken)).Content);
    }
}
