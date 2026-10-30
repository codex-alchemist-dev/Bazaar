using System.Text;
using Bazaar.Server.Backup.Manifest;

namespace Bazaar.Server.Tests.Backup.Manifest;

public sealed class BundleLayoutValidatorTests
{
    private static (string Path, byte[] Bytes)[] Payload() =>
    [
        ("vault/index.md", Encoding.UTF8.GetBytes("# Index\n")),
        ("soul/charter.json", Encoding.UTF8.GetBytes("{}")),
    ];

    private static (string Path, byte[]) WithManifest((string Path, byte[] Bytes)[] payload) =>
        (BundleLayoutValidator.ManifestPath, ManifestCodec.Serialize(ManifestWriter.Build(payload)));

    [Fact]
    public void Valid_bundle_with_manifest_passes()
    {
        var payload = Payload();
        var files = payload.Append(WithManifest(payload)).ToArray();

        var result = BundleLayoutValidator.Validate(files);

        Assert.True(result.IsValid);
        Assert.Empty(result.LayoutFindings);
        Assert.NotNull(result.Contents);
        Assert.True(result.Contents.IsValid);
    }

    [Fact]
    public void Missing_manifest_is_a_layout_finding_and_skips_content_checks()
    {
        var result = BundleLayoutValidator.Validate(Payload());

        Assert.False(result.IsValid);
        var finding = Assert.Single(result.LayoutFindings);
        Assert.Equal(BundleLayoutFindingKind.ManifestMissing, finding.Kind);
        Assert.Null(result.Contents);
    }

    [Fact]
    public void Malformed_manifest_is_a_layout_finding()
    {
        var files = Payload().Append((BundleLayoutValidator.ManifestPath, Encoding.UTF8.GetBytes("not a manifest\n"))).ToArray();

        var result = BundleLayoutValidator.Validate(files);

        Assert.False(result.IsValid);
        Assert.Equal(BundleLayoutFindingKind.ManifestMalformed, Assert.Single(result.LayoutFindings).Kind);
        Assert.Null(result.Contents);
    }

    [Fact]
    public void Manifest_without_trailing_newline_is_malformed()
    {
        var text = Encoding.UTF8.GetString(ManifestCodec.Serialize(ManifestWriter.Build(Payload())));
        var files = Payload().Append((BundleLayoutValidator.ManifestPath, Encoding.UTF8.GetBytes(text.TrimEnd('\n')))).ToArray();

        var result = BundleLayoutValidator.Validate(files);

        Assert.Equal(BundleLayoutFindingKind.ManifestMalformed, Assert.Single(result.LayoutFindings).Kind);
    }

    [Fact]
    public void Manifest_that_lists_itself_is_malformed()
    {
        var payload = Payload();
        var manifest = ManifestWriter.Build(payload.Append((BundleLayoutValidator.ManifestPath, [0])).ToArray());
        var files = payload.Append((BundleLayoutValidator.ManifestPath, ManifestCodec.Serialize(manifest))).ToArray();

        var result = BundleLayoutValidator.Validate(files);

        Assert.Equal(BundleLayoutFindingKind.ManifestMalformed, Assert.Single(result.LayoutFindings).Kind);
    }

    [Fact]
    public void Payload_that_differs_from_the_manifest_is_reported_by_contents()
    {
        var payload = Payload();
        var files = payload.Append(WithManifest(payload)).ToArray();
        files[0] = ("vault/index.md", Encoding.UTF8.GetBytes("changed\n"));

        var result = BundleLayoutValidator.Validate(files);

        Assert.Empty(result.LayoutFindings);
        Assert.NotNull(result.Contents);
        Assert.Equal(ManifestFindingKind.HashMismatch, Assert.Single(result.Contents.Findings).Kind);
    }

    [Fact]
    public void Codec_round_trip_preserves_the_manifest()
    {
        var manifest = ManifestWriter.Build(Payload());

        var parsed = ManifestCodec.TryParse(ManifestCodec.Serialize(manifest));

        Assert.Null(parsed.Error);
        Assert.Equal(manifest.Entries, parsed.Manifest!.Entries);
    }

    [Fact]
    public void Codec_rejects_unsorted_entries()
    {
        var text = "bazaar-backup-manifest/1 SHA-256\n"
            + "sha256 " + new string('a', 64) + " 1 b.txt\n"
            + "sha256 " + new string('b', 64) + " 1 a.txt\n";

        var parsed = ManifestCodec.TryParse(Encoding.UTF8.GetBytes(text));

        Assert.Null(parsed.Manifest);
        Assert.Contains("out of order", parsed.Error);
    }
}
