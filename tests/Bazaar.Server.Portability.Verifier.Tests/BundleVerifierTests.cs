using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Verifier.Tests;

/// <summary>Doc 44 section 15, Chains: reject a changed event, a changed manifest line, a missing checkpoint and a broken proof; accept an untouched bundle; reject a wrong signing key.</summary>
public class BundleVerifierTests
{
    private static string[] Codes(VerifyReport report) => report.Problems.Select(p => p.Code).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static BundleFile Edit(BundleFile file, string path, string from, string to)
    {
        if (file.Path != path)
        {
            return file;
        }

        var text = Encoding.UTF8.GetString(file.Content);
        Assert.Contains(from, text);
        return file with { Content = Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal)) };
    }

    [Fact]
    public void Untouched_bundle_verifies()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();

        var report = f.Run();

        Assert.True(report.Verified, string.Join("; ", report.Problems));
        Assert.Empty(report.Problems);
        Assert.Empty(report.UnlistedFiles);
        Assert.All(report.Checks, c => Assert.Equal("passed", c.Status));
        Assert.Equal(7, report.Audit!.Rows);
        Assert.Equal(2, report.Audit.Checkpoints);
        Assert.Equal(1, report.Audit.UncheckpointedTailRows);
        Assert.Equal(2, report.Audit.ProofsChecked);
        Assert.Equal(2, report.Gc!.Gcs);
        Assert.Equal(6, report.Gc.Events);
        Assert.Equal("bundle", report.SigningKeySource);
        Assert.Equal(report.FilesListed, report.FilesHashed);
    }

    [Fact]
    public void Report_json_is_deterministic_snake_case()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();

        var first = f.Run().ToJson();
        var second = f.Run().ToJson();

        Assert.Equal(first, second);
        using var doc = JsonDocument.Parse(first);
        Assert.Equal("bazaar.verify.report.v1", doc.RootElement.GetProperty("format").GetString());
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("not_checked", out _));
        Assert.DoesNotContain("\r", first);
        Assert.DoesNotContain(f.Directory, first);
    }

    [Fact]
    public void Changed_audit_event_on_disk_fails_its_hash()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        var path = f.PathOf("audit/entries.jsonl");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"actor3\"", "\"actor9\"", StringComparison.Ordinal));

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Contains("sha256_mismatch", Codes(report));
    }

    [Fact]
    public void Changed_audit_event_with_a_repaired_manifest_fails_the_chain()
    {
        using var f = new BundleFixture();
        f.WriteWith(file => Edit(file, "audit/entries.jsonl", "\"actor3\"", "\"actor9\""));

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Equal(["audit_chain_hashmismatch"], Codes(report));
    }

    [Fact]
    public void Changed_gc_event_hash_breaks_the_gc_chain()
    {
        using var f = new BundleFixture();
        var files = f.BaseFiles();
        var events = files.First(x => x.Path == "gc/g1/events.jsonl");
        var lines = Encoding.UTF8.GetString(events.Content).Split('\n');
        var third = lines[2].Replace("\"prev_event_hash\":\"", "\"prev_event_hash\":\"00", StringComparison.Ordinal);
        lines[2] = third;
        files[files.IndexOf(events)] = events with { Content = Encoding.UTF8.GetBytes(string.Join('\n', lines)) };
        f.Write(files);

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Contains("gc_chain_broken", Codes(report));
    }

    [Fact]
    public void Removed_gc_event_breaks_the_sequence()
    {
        using var f = new BundleFixture();
        var files = f.BaseFiles();
        var events = files.First(x => x.Path == "gc/g2/events.jsonl");
        var first = Encoding.UTF8.GetString(events.Content).Split('\n')[0] + "\n";
        files[files.IndexOf(events)] = events with { Content = Encoding.UTF8.GetBytes(first + first.Replace("\"seq\":1", "\"seq\":3", StringComparison.Ordinal)) };
        f.Write(files);

        var report = f.Run();

        Assert.Contains("gc_sequence_broken", Codes(report));
    }

    [Fact]
    public void Gc_head_that_does_not_match_the_events_fails()
    {
        using var f = new BundleFixture();
        f.WriteWith(file => Edit(file, "gc/g1/head.json", "\"last_seq\":4", "\"last_seq\":5"));

        var report = f.Run();

        Assert.Contains("gc_head_mismatch", Codes(report));
    }

    [Fact]
    public void Changed_manifest_line_fails_the_signature()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        var path = f.PathOf("manifest.json");
        var manifest = File.ReadAllText(path);
        var entry = "\"path\":\"config/settings.json\"";
        Assert.Contains(entry, manifest);
        File.WriteAllText(path, manifest.Replace(entry, "\"path\":\"config/settings.jsoN\"", StringComparison.Ordinal));

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Contains("signature_invalid", Codes(report));
        Assert.Contains("file_missing", Codes(report));
    }

    [Fact]
    public void Missing_middle_checkpoint_fails_the_replay()
    {
        using var f = new BundleFixture();
        f.WriteWith(file => file.Path == "audit/checkpoints.jsonl"
            ? file with { Content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Content).Split('\n')[1] + "\n") }
            : file);

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Equal(["audit_chain_checkpointrangebroken"], Codes(report));
    }

    [Fact]
    public void Missing_last_checkpoint_fails_against_the_archive_marker()
    {
        using var f = new BundleFixture();
        f.WriteWith(file => file.Path == "audit/checkpoints.jsonl"
            ? file with { Content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Content).Split('\n')[0] + "\n") }
            : file);

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Equal(["audit_archive_mismatch"], Codes(report));
    }

    [Fact]
    public void Broken_proof_fails()
    {
        using var f = new BundleFixture();
        var proofPath = f.BaseFiles().First(x => x.Path.StartsWith("audit/proofs/", StringComparison.Ordinal)).Path;
        f.WriteWith(file => Edit(file, proofPath, "\"leaf_index\": 1", "\"leaf_index\": 0"));

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Equal(["audit_proof_invalid"], Codes(report));
        Assert.Equal(proofPath, report.Problems[0].Path);
    }

    [Fact]
    public void Wrong_pinned_signing_key_is_rejected_and_the_right_one_accepted()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var wrong = f.Run(f.Options with { PinnedSigningKey = other.ExportSubjectPublicKeyInfo() });
        var right = f.Run(f.Options with { PinnedSigningKey = f.ExportPublicKey });

        Assert.False(wrong.Verified);
        Assert.Equal(["signature_invalid"], Codes(wrong));
        Assert.Equal("pinned", wrong.SigningKeySource);
        Assert.True(right.Verified);
    }

    [Fact]
    public void Bundle_signed_by_another_key_with_its_own_public_key_verifies_only_when_not_pinned()
    {
        using var f = new BundleFixture();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        f.Write(f.BaseFiles(), attacker);

        Assert.True(f.Run().Verified);
        Assert.False(f.Run(f.Options with { PinnedSigningKey = f.ExportPublicKey }).Verified);
    }

    [Fact]
    public void Unsigned_bundle_fails()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        File.Delete(f.PathOf("manifest.sig"));

        var report = f.Run();

        Assert.Contains("manifest_unsigned", Codes(report));
    }

    [Fact]
    public void Unlisted_file_is_reported()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        File.WriteAllText(f.PathOf("gc/extra.txt"), "x");
        File.WriteAllText(f.PathOf("stray.bin"), "y");

        var report = f.Run();

        Assert.False(report.Verified);
        Assert.Equal(["gc/extra.txt", "stray.bin"], report.UnlistedFiles);
        Assert.Contains("file_not_in_manifest", Codes(report));
    }

    [Fact]
    public void Missing_listed_file_is_reported()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        File.Delete(f.PathOf("config/settings.json"));

        var report = f.Run();

        Assert.Equal(["file_missing"], Codes(report));
    }

    [Fact]
    public void Wrong_audit_key_fails_the_checkpoint_signatures_and_a_missing_one_fails_closed()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var wrong = f.Run(new VerifyOptions(AuditKey: other.ExportSubjectPublicKeyInfo()));
        var none = f.Run(new VerifyOptions());

        Assert.Contains("audit_chain_checkpointsignatureinvalid", Codes(wrong));
        Assert.Contains("audit_proof_invalid", Codes(wrong));
        Assert.Equal(["audit_key_required"], Codes(none));
        Assert.False(none.Verified);
    }

    [Fact]
    public void Manifest_with_files_out_of_order_or_a_traversal_path_fails()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        var path = f.PathOf("manifest.json");
        var manifest = File.ReadAllText(path).Replace("\"path\":\"config/settings.json\"", "\"path\":\"../settings.json\"", StringComparison.Ordinal);
        File.WriteAllText(path, manifest);

        var report = f.Run();

        Assert.Contains("manifest_entry_invalid", Codes(report));
    }

    [Fact]
    public void Bundle_without_audit_or_gc_data_skips_those_checks()
    {
        using var f = new BundleFixture();
        f.Write([new BundleFile("config/settings.json", "{}"u8.ToArray())]);

        var report = f.Run(new VerifyOptions());

        Assert.True(report.Verified);
        Assert.Contains(new VerifyCheck("audit_chain", "skipped"), report.Checks);
        Assert.Contains(new VerifyCheck("gc_chains", "skipped"), report.Checks);
        Assert.Null(report.Audit);
    }

    [Fact]
    public void Large_gc_log_streams_without_holding_it()
    {
        using var f = new BundleFixture();
        var files = new List<BundleFile>();
        var big = new StringBuilder();
        string? prev = null;
        const int count = 50_000;
        for (var i = 1; i <= count; i++)
        {
            var hash = i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            big.Append("{\"seq\":").Append(i).Append(",\"prev_event_hash\":").Append(prev is null ? "null" : "\"" + prev + "\"")
                .Append(",\"event_hash\":\"").Append(hash).Append("\"}\n");
            prev = hash;
        }

        files.Add(new BundleFile("gc/big/events.jsonl", Encoding.UTF8.GetBytes(big.ToString())));
        files.Add(new BundleFile("gc/big/head.json", Encoding.UTF8.GetBytes("{\"last_seq\":" + count + ",\"head_hash\":\"" + prev + "\",\"counts\":{\"events\":" + count + "}}")));
        f.Write(files);

        var report = f.Run(new VerifyOptions());

        Assert.True(report.Verified, string.Join("; ", report.Problems));
        Assert.Equal(count, report.Gc!.Events);
    }
}
