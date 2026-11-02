using System.Text;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Import;

namespace Bazaar.Server.Tests.Souls.Import;

public sealed class BundleImporterTests
{
    private const string _zeros = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Valid_bundle_is_quarantined_with_its_verified_manifest()
    {
        var bundle = new BundleBuilder().AddFile("charter.md", "# Charter").AddFile("prefs/tone.json", "{}");
        var (manifest, signature) = bundle.Sealed();

        var result = Run(manifest, signature, bundle.Files);

        var quarantined = Assert.IsType<BundleQuarantined>(result);
        Assert.Equal(BundleBuilder.SoulId, quarantined.Manifest.SoulId);
        Assert.Equal(3, quarantined.Manifest.SoulVersion);
        Assert.Equal("charter.md|prefs/tone.json", string.Join('|', quarantined.Manifest.Files.Select(f => f.Path)));
    }

    [Fact]
    public void Bundle_with_no_files_is_quarantined()
    {
        var (manifest, signature) = new BundleBuilder().Sealed();

        Assert.IsType<BundleQuarantined>(Run(manifest, signature, []));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var (manifest, signature) = Sign("{not json");

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"formatVersion\"")]
    [InlineData("{\"soulId\":\"x\"}")]
    public void Manifest_that_is_not_an_object_with_a_format_version_is_rejected(string json)
    {
        var (manifest, signature) = Sign(json);

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Unknown_format_version_is_rejected()
    {
        var (manifest, signature) = Signed(bundle => bundle.ManifestBytes(), Replace("\"formatVersion\":1", "\"formatVersion\":2"));

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.UnknownFormatVersion);
    }

    [Fact]
    public void Non_integer_format_version_is_malformed()
    {
        var (manifest, signature) = Signed(bundle => bundle.ManifestBytes(), Replace("\"formatVersion\":1", "\"formatVersion\":\"1\""));

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Unknown_member_in_version_one_is_rejected()
    {
        var (manifest, signature) = Signed(bundle => bundle.ManifestBytes(), Replace("{\"formatVersion\":1,", "{\"formatVersion\":1,\"extra\":true,"));

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Manifest_without_files_is_rejected()
    {
        var json = $"{{\"formatVersion\":1,\"soulId\":\"{BundleBuilder.SoulId}\",\"soulVersion\":3,\"contentSha256\":\"{_zeros}\",\"exporter\":\"x\",\"exportedAt\":\"2026-10-30T12:00:00+00:00\",\"exclusions\":[]}}";
        var (manifest, signature) = Sign(json);

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Manifest_with_empty_soul_id_is_rejected()
    {
        var (manifest, signature) = Signed(bundle => bundle.ManifestBytes(), Replace(BundleBuilder.SoulId.ToString(), Guid.Empty.ToString()));

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Manifest_with_uppercase_content_hash_is_rejected()
    {
        var lowerHash = BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("content"));
        var (manifest, signature) = Signed(bundle => bundle.ManifestBytes(), Replace(lowerHash, lowerHash.ToUpperInvariant()));

        AssertRejected(Run(manifest, signature, []), BundleRejectionCode.ManifestMalformed);
    }

    [Fact]
    public void Manifest_over_the_manifest_cap_is_rejected_before_parsing()
    {
        var (manifest, signature) = new BundleBuilder().Sealed();

        var result = Run(manifest, signature, [], new BundleImportOptions { MaxManifestBytes = 10 });

        AssertRejected(result, BundleRejectionCode.ManifestTooLarge);
    }

    [Fact]
    public void Missing_signature_is_rejected()
    {
        var (manifest, _) = new BundleBuilder().AddFile("a.txt", "x").Sealed();

        AssertRejected(Run(manifest, null, []), BundleRejectionCode.SignatureMissing);
    }

    [Fact]
    public void Signature_without_key_id_is_rejected_as_missing()
    {
        var (manifest, signature) = new BundleBuilder().AddFile("a.txt", "x").Sealed();

        AssertRejected(Run(manifest, signature with { KeyId = "" }, []), BundleRejectionCode.SignatureMissing);
    }

    [Fact]
    public void Signature_from_an_unknown_key_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        var manifest = bundle.ManifestBytes();
        var signature = FakeBundleSignatureVerifier.Sign("someone-else", Encoding.UTF8.GetBytes("other"), manifest);

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.UnknownSigningKey);
    }

    [Fact]
    public void Manifest_changed_after_signing_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        var (manifest, signature) = bundle.Sealed();
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("test-exporter", "evil-exporter"));

        AssertRejected(Run(tampered, signature, bundle.Files), BundleRejectionCode.SignatureInvalid);
    }

    [Fact]
    public void Signature_with_the_wrong_algorithm_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature with { Algorithm = "Ed25519" }, bundle.Files), BundleRejectionCode.SignatureInvalid);
    }

    [Fact]
    public void Signature_that_is_not_base64_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature with { SignatureBase64 = "%%%not base64" }, bundle.Files), BundleRejectionCode.SignatureInvalid);
    }

    [Fact]
    public void Failed_signature_stops_before_the_file_checks()
    {
        var bundle = new BundleBuilder().AddUnlistedFile("../escape.txt", "x");
        var manifest = bundle.ManifestBytes();
        var signature = FakeBundleSignatureVerifier.Sign(BundleBuilder.KeyId, Encoding.UTF8.GetBytes("wrong key"), manifest);

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.SignatureInvalid);
    }

    [Theory]
    [InlineData("/etc/passwd", BundleRejectionCode.PathAbsolute)]
    [InlineData("../escape.txt", BundleRejectionCode.PathTraversal)]
    [InlineData("a/../b.txt", BundleRejectionCode.PathTraversal)]
    [InlineData("file..name.txt", BundleRejectionCode.PathTraversal)]
    [InlineData("dir\\file.txt", BundleRejectionCode.PathBackslash)]
    [InlineData("\\\\server\\share", BundleRejectionCode.PathBackslash)]
    [InlineData("C:/windows/win.ini", BundleRejectionCode.PathDriveLetter)]
    [InlineData("c:x", BundleRejectionCode.PathDriveLetter)]
    [InlineData("", BundleRejectionCode.PathInvalid)]
    [InlineData("a//b.txt", BundleRejectionCode.PathInvalid)]
    [InlineData("./a.txt", BundleRejectionCode.PathInvalid)]
    [InlineData("dir/", BundleRejectionCode.PathInvalid)]
    [InlineData("ab:stream", BundleRejectionCode.PathInvalid)]
    [InlineData("tab\tname", BundleRejectionCode.PathInvalid)]
    public void Unsafe_paths_are_rejected(string path, BundleRejectionCode code)
    {
        var bundle = new BundleBuilder().AddFile(path, "x");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), code);
    }

    [Fact]
    public void Duplicate_path_in_the_manifest_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        bundle.AddEntryOnly("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("x")), 1);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.PathDuplicate);
    }

    [Fact]
    public void Duplicate_path_among_the_received_files_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x");
        var (manifest, signature) = bundle.Sealed();
        var files = new List<BundleFileContent>(bundle.Files) { new("a.txt", Encoding.UTF8.GetBytes("x")) };

        AssertRejected(Run(manifest, signature, files), BundleRejectionCode.PathDuplicate);
    }

    [Fact]
    public void Paths_that_differ_only_in_case_are_duplicates()
    {
        var bundle = new BundleBuilder().AddFile("Readme.md", "x").AddFile("readme.md", "y");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.PathDuplicate);
    }

    [Fact]
    public void File_not_listed_in_the_manifest_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x").AddUnlistedFile("extra.txt", "z");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.FileNotInManifest);
    }

    [Fact]
    public void File_whose_path_differs_only_in_case_from_the_manifest_is_not_matched()
    {
        var bundle = new BundleBuilder().AddFile("Readme.md", "x");
        var (manifest, signature) = bundle.Sealed();
        var files = new List<BundleFileContent> { new("readme.md", Encoding.UTF8.GetBytes("x")) };

        AssertRejected(Run(manifest, signature, files), BundleRejectionCode.FileNotInManifest, BundleRejectionCode.ManifestEntryMissingFile);
    }

    [Fact]
    public void Manifest_entry_with_no_file_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x").AddEntryOnly("missing.txt", _zeros, 0);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.ManifestEntryMissingFile);
    }

    [Fact]
    public void Content_that_does_not_match_its_digest_is_rejected()
    {
        var entry = new BundleFile("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("y")), 1);
        var bundle = new BundleBuilder().AddFileWithEntry("a.txt", "x", entry);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.DigestMismatch);
    }

    [Fact]
    public void Uppercase_digest_is_malformed()
    {
        var entry = new BundleFile("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("x")).ToUpperInvariant(), 1);
        var bundle = new BundleBuilder().AddFileWithEntry("a.txt", "x", entry);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.DigestMalformed);
    }

    [Fact]
    public void Digest_of_the_wrong_length_is_malformed()
    {
        var entry = new BundleFile("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("x"))[..63], 1);
        var bundle = new BundleBuilder().AddFileWithEntry("a.txt", "x", entry);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.DigestMalformed);
    }

    [Fact]
    public void Manifest_size_that_differs_from_the_file_is_rejected()
    {
        var entry = new BundleFile("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("x")), 2);
        var bundle = new BundleBuilder().AddFileWithEntry("a.txt", "x", entry);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.SizeMismatch);
    }

    [Fact]
    public void Negative_manifest_size_is_rejected()
    {
        var entry = new BundleFile("a.txt", BundleBuilder.Sha256Hex(Encoding.UTF8.GetBytes("x")), -1);
        var bundle = new BundleBuilder().AddFileWithEntry("a.txt", "x", entry);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files), BundleRejectionCode.SizeMismatch);
    }

    [Fact]
    public void File_at_the_per_file_cap_is_accepted()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x").AddFile("b.txt", "y");
        var (manifest, signature) = bundle.Sealed();

        var result = Run(manifest, signature, bundle.Files, new BundleImportOptions { MaxFileBytes = 1, MaxTotalBytes = 2 });

        Assert.IsType<BundleQuarantined>(result);
    }

    [Fact]
    public void File_over_the_per_file_cap_is_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "abcd");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files, new BundleImportOptions { MaxFileBytes = 3 }), BundleRejectionCode.FileTooLarge);
    }

    [Fact]
    public void Files_over_the_total_cap_are_rejected()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "abc").AddFile("b.txt", "defg");
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(Run(manifest, signature, bundle.Files, new BundleImportOptions { MaxTotalBytes = 5 }), BundleRejectionCode.TotalTooLarge);
    }

    [Theory]
    [InlineData(0L, 1L, 1L)]
    [InlineData(1L, -1L, 1L)]
    [InlineData(1L, 1L, 0L)]
    public void Non_positive_caps_are_refused_at_construction(long manifestBytes, long fileBytes, long totalBytes)
    {
        var options = new BundleImportOptions { MaxManifestBytes = manifestBytes, MaxFileBytes = fileBytes, MaxTotalBytes = totalBytes };

        Assert.Throws<ArgumentOutOfRangeException>(() => new BundleImporter(BundleBuilder.Verifier, options));
    }

    [Fact]
    public void Every_failing_file_check_is_reported_together()
    {
        var bundle = new BundleBuilder().AddFile("a.txt", "x").AddUnlistedFile("../b.txt", "z").AddEntryOnly("gone.txt", _zeros, 0);
        var (manifest, signature) = bundle.Sealed();

        AssertRejected(
            Run(manifest, signature, bundle.Files),
            BundleRejectionCode.PathTraversal,
            BundleRejectionCode.FileNotInManifest,
            BundleRejectionCode.ManifestEntryMissingFile);
    }

    private static BundleImportResult Run(
        byte[] manifest,
        BundleSignature? signature,
        IReadOnlyList<BundleFileContent> files,
        BundleImportOptions? options = null) =>
        new BundleImporter(BundleBuilder.Verifier, options).Import(manifest, signature, files);

    private static (byte[] Manifest, BundleSignature Signature) Sign(string json)
    {
        var manifest = Encoding.UTF8.GetBytes(json);
        return (manifest, FakeBundleSignatureVerifier.Sign(BundleBuilder.KeyId, BundleBuilder.Key, manifest));
    }

    private static (byte[] Manifest, BundleSignature Signature) Signed(Func<BundleBuilder, byte[]> build, Func<string, string> edit)
    {
        var text = edit(Encoding.UTF8.GetString(build(new BundleBuilder())));
        return Sign(text);
    }

    private static Func<string, string> Replace(string oldValue, string newValue) => text => text.Replace(oldValue, newValue, StringComparison.Ordinal);

    private static void AssertRejected(BundleImportResult result, params BundleRejectionCode[] expected)
    {
        var rejected = Assert.IsType<BundleRejected>(result);
        var actual = rejected.Rejections.Select(r => r.Code).Distinct().OrderBy(c => c).ToArray();
        Assert.Equal(expected.Distinct().OrderBy(c => c).ToArray(), actual);
    }
}
