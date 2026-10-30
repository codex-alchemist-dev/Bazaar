using System.Text;
using Bazaar.Server.Backup.Manifest;

namespace Bazaar.Server.Tests.Backup.Manifest;

public sealed class ManifestVerifierTests
{
    private static (string Path, byte[] Bytes)[] Sample() =>
    [
        ("vault/index.md", Encoding.UTF8.GetBytes("# Index\n")),
        ("soul/charter.json", Encoding.UTF8.GetBytes("{\"name\":\"x\"}")),
        ("db/base.tar", [1, 2, 3, 4]),
    ];

    [Fact]
    public void Round_trip_write_then_verify_gives_no_findings()
    {
        var files = Sample();
        var manifest = ManifestWriter.Build(files);

        var result = ManifestVerifier.Verify(manifest, files);

        Assert.True(result.IsValid);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void A_changed_byte_gives_one_HashMismatch()
    {
        var files = Sample();
        var manifest = ManifestWriter.Build(files);
        var tampered = Sample();
        tampered[2].Bytes[0] ^= 0xFF;

        var result = ManifestVerifier.Verify(manifest, tampered);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(ManifestFindingKind.HashMismatch, finding.Kind);
        Assert.Equal("db/base.tar", finding.Path);
        Assert.NotEqual(finding.Expected, finding.Actual);
    }

    [Fact]
    public void A_deleted_file_gives_Missing()
    {
        var files = Sample();
        var manifest = ManifestWriter.Build(files);

        var result = ManifestVerifier.Verify(manifest, files.Where(f => f.Path != "soul/charter.json"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(ManifestFindingKind.Missing, finding.Kind);
        Assert.Equal("soul/charter.json", finding.Path);
    }

    [Fact]
    public void An_added_file_gives_Unexpected()
    {
        var files = Sample();
        var manifest = ManifestWriter.Build(files);
        var extra = files.Append(("vault/extra.md", [9])).ToArray();

        var result = ManifestVerifier.Verify(manifest, extra);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(ManifestFindingKind.Unexpected, finding.Kind);
        Assert.Equal("vault/extra.md", finding.Path);
    }

    [Fact]
    public void Findings_are_sorted_by_path_then_kind()
    {
        var manifest = ManifestWriter.Build([("b.txt", [1]), ("a.txt", [1])]);

        var result = ManifestVerifier.Verify(manifest, [("c.txt", [1]), ("b.txt", [2])]);

        Assert.Equal(
            [("a.txt", ManifestFindingKind.Missing), ("b.txt", ManifestFindingKind.HashMismatch), ("c.txt", ManifestFindingKind.Unexpected)],
            result.Findings.Select(f => (f.Path, f.Kind)));
    }

    [Fact]
    public void Backslash_paths_in_the_files_match_manifest_entries()
    {
        var manifest = ManifestWriter.Build([("vault/a.md", [5])]);

        var result = ManifestVerifier.Verify(manifest, [("vault\\a.md", [5])]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parent_paths_in_the_files_are_rejected()
    {
        var manifest = ManifestWriter.Build([("a.md", [5])]);

        Assert.Throws<ArgumentException>(() => ManifestVerifier.Verify(manifest, [("../a.md", [5])]));
    }
}
