using System.Buffers;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Tests.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Contracts.Tests.Releases;

/// <summary>Release manifest records (doc 30 sections 4, 5 and 7): validation, immutability, wire round trip, golden JSON.</summary>
public class ReleaseManifestTests
{
    private const string _sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string _fingerprint = "0123456789abcdef0123456789abcdef01234567";

    private static readonly JsonSerializerOptions _indented = new(WireJsonContext.Default.Options) { WriteIndented = true, NewLine = "\n" };

    private static ReleaseArtifact Artifact(
        string os = "linux", string arch = "linux-x64", string format = "tar.gz",
        string url = "https://releases.example.org/v0.1.0/Bazaar-0.1.0-linux-x64.tar.gz",
        long size = 104857600, string sha = _sha,
        string shaUrl = "https://releases.example.org/v0.1.0/Bazaar-0.1.0-linux-x64.tar.gz.sha256",
        string sigUrl = "https://releases.example.org/v0.1.0/Bazaar-0.1.0-linux-x64.tar.gz.asc") =>
        new(os, arch, format, url, size, sha, shaUrl, sigUrl);

    private static ReleaseManifest Manifest(
        string channel = "Stable", string version = "0.1.0", string min = "1.0", string max = "1.2",
        string schema = "7", string fingerprint = _fingerprint, IReadOnlyList<ReleaseArtifact>? artifacts = null) =>
        new(channel, version, min, max, schema, fingerprint, artifacts ?? [Artifact()]);

    private static ReleaseManifest Sample() => Manifest(artifacts:
    [
        Artifact(),
        Artifact(arch: "linux-arm64", url: "Bazaar-0.1.0-linux-arm64.tar.gz",
            shaUrl: "Bazaar-0.1.0-linux-arm64.tar.gz.sha256", sigUrl: "Bazaar-0.1.0-linux-arm64.tar.gz.asc",
            sha: "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"),
    ]);

    [Fact]
    public void A_valid_manifest_exposes_its_fields_and_finds_artifacts()
    {
        var m = Sample();

        Assert.Equal("Stable", m.Channel);
        Assert.Equal(2, m.Artifacts.Count);
        Assert.Equal("linux-arm64", m.Find("linux-arm64", "tar.gz")!.Architecture);
        Assert.Null(m.Find("linux-x64", "appimage"));
    }

    [Theory]
    [InlineData("LTSC", "1.4.2")]
    [InlineData("Stable", "0.1.0")]
    [InlineData("Stable", "0.1.0+a1b2c3d")]
    [InlineData("Beta", "0.2.0-beta.1")]
    [InlineData("Beta", "0.2.0-rc.3")]
    [InlineData("Alpha", "0.3.0-alpha.12")]
    [InlineData("Latest-Commit", "0.3.0-dev.4+a1b2c3d")]
    public void Every_channel_accepts_its_own_versions(string channel, string version) =>
        Assert.Equal(channel, Manifest(channel: channel, version: version).Channel);

    [Theory]
    [InlineData("stable", "0.1.0")]
    [InlineData("Nightly", "0.1.0")]
    [InlineData("", "0.1.0")]
    [InlineData("Stable", "0.1.0-beta.1")]
    [InlineData("LTSC", "1.0.0-rc.1")]
    [InlineData("Beta", "0.1.0")]
    [InlineData("Beta", "0.1.0-alpha.1")]
    [InlineData("Alpha", "0.1.0-dev.1")]
    [InlineData("Latest-Commit", "0.1.0")]
    [InlineData("Stable", "")]
    [InlineData("Stable", "v0.1.0")]
    [InlineData("Stable", "1.0")]
    [InlineData("Stable", "01.0.0")]
    public void Unknown_channels_and_mismatched_or_malformed_versions_are_refused(string channel, string version) =>
        Assert.Throws<ArgumentException>(() => Manifest(channel: channel, version: version));

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1.0", "1")]
    [InlineData("1.0.0", "1.0")]
    [InlineData("v1.0", "1.0")]
    [InlineData("1.01", "1.2")]
    [InlineData("1.3", "1.2")]
    [InlineData("2.0", "1.9")]
    [InlineData("", "1.0")]
    public void Protocol_range_must_be_major_minor_and_ordered(string min, string max) =>
        Assert.Throws<ArgumentException>(() => Manifest(min: min, max: max));

    [Fact]
    public void Protocol_range_may_be_one_version_or_span_majors()
    {
        Assert.Equal("1.0", Manifest(min: "1.0", max: "1.0").MaxProtocol);
        Assert.Equal("2.1", Manifest(min: "1.9", max: "2.1").MaxProtocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 7")]
    [InlineData("-1")]
    [InlineData("a b")]
    [InlineData("123456789012345678901234567890123")]
    public void Schema_version_must_be_a_short_token(string schema) =>
        Assert.Throws<ArgumentException>(() => Manifest(schema: schema));

    [Fact]
    public void Schema_version_accepts_a_real_schema_digest()
    {
        string digest = SchemaDigest.Compute([("common", 3), ("kodo", 1)]);

        Assert.Equal(71, digest.Length);
        Assert.Equal(digest, Manifest(schema: digest).SchemaVersion);
    }

    [Theory]
    [InlineData("sha256:")] // No hex.
    [InlineData("sha256:0123456789abcdef")] // Too short.
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")] // Uppercase hex.
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")] // Not hex.
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // Too long.
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // Wrong prefix.
    [InlineData("SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // Prefix is case sensitive.
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // No prefix, over 32 characters.
    public void Schema_version_refuses_a_malformed_digest(string schema) =>
        Assert.Throws<ArgumentException>(() => Manifest(schema: schema));

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    public void Fingerprint_must_be_lowercase_hex_of_40_or_64_characters(string fingerprint) =>
        Assert.Throws<ArgumentException>(() => Manifest(fingerprint: fingerprint));

    [Fact]
    public void A_64_character_fingerprint_is_accepted() =>
        Assert.Equal(_sha, Manifest(fingerprint: _sha).SigningKeyFingerprint);

    [Fact]
    public void Artifacts_must_be_present_unique_and_non_null()
    {
        Assert.Throws<ArgumentException>(() => Manifest(artifacts: []));
        Assert.Throws<ArgumentException>(() => Manifest(artifacts: [Artifact(), Artifact()]));
        Assert.Throws<ArgumentException>(() => Manifest(artifacts: [null!]));
        Assert.Throws<ArgumentNullException>(() => new ReleaseManifest("Stable", "0.1.0", "1.0", "1.0", "7", _fingerprint, null!));
    }

    [Theory]
    [InlineData("windows", "linux-x64", "tar.gz")]
    [InlineData("linux", "linux-riscv64", "tar.gz")]
    [InlineData("linux", "win-x64", "tar.gz")]
    [InlineData("linux", "Linux-x64", "tar.gz")]
    [InlineData("linux", "linux-x64", "deb")]
    [InlineData("linux", "linux-x64", "")]
    public void Artifact_os_architecture_and_format_are_validated(string os, string arch, string format) =>
        Assert.Throws<ArgumentException>(() => Artifact(os: os, arch: arch, format: format));

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Sha256_must_be_64_lowercase_hex_characters(string sha) =>
        Assert.Throws<ArgumentException>(() => Artifact(sha: sha));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(4L * 1024 * 1024 * 1024 + 1)]
    public void Size_must_be_positive_and_bounded(long size) =>
        Assert.Throws<ArgumentException>(() => Artifact(size: size));

    [Theory]
    [InlineData("https://releases.example.org/a.tar.gz")]
    [InlineData("HTTPS://releases.example.org/a.tar.gz")]
    [InlineData("a.tar.gz")]
    [InlineData("releases/v0.1.0/a.tar.gz")]
    [InlineData("/releases/a.tar.gz")]
    public void Https_and_relative_urls_are_accepted(string url)
    {
        Assert.Equal(url, Artifact(url: url).Url);
        Assert.Equal(url, Artifact(shaUrl: url).Sha256Url);
        Assert.Equal(url, Artifact(sigUrl: url).SignatureUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://releases.example.org/a.tar.gz")]
    [InlineData("ftp://releases.example.org/a.tar.gz")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//releases.example.org/a.tar.gz")]
    [InlineData("https://")]
    [InlineData("https://user:pw@releases.example.org/a")]
    [InlineData("https://releases.example.org/a?token=abc")]
    [InlineData("https://releases.example.org/a#frag")]
    [InlineData("../a.tar.gz")]
    [InlineData("releases/../../a.tar.gz")]
    [InlineData("a b.tar.gz")]
    [InlineData("releases\\a.tar.gz")]
    [InlineData("c:/a.tar.gz")]
    [InlineData("a.tar.gz?x=1")]
    public void Other_urls_are_refused_for_every_url_field(string url)
    {
        Assert.Throws<ArgumentException>(() => Artifact(url: url));
        Assert.Throws<ArgumentException>(() => Artifact(shaUrl: url));
        Assert.Throws<ArgumentException>(() => Artifact(sigUrl: url));
    }

    [Fact]
    public void The_artifact_list_is_a_private_copy_and_read_only()
    {
        var source = new List<ReleaseArtifact> { Artifact() };
        var m = Manifest(artifacts: source);

        source.Add(Artifact(arch: "linux-arm64"));

        Assert.Single(m.Artifacts);
        Assert.False(m.Artifacts is IList<ReleaseArtifact> { IsReadOnly: false });
    }

    [Fact]
    public void Manifests_with_equal_content_are_equal()
    {
        Assert.Equal(Sample(), Sample());
        Assert.Equal(Sample().GetHashCode(), Sample().GetHashCode());
        Assert.NotEqual(Sample(), Manifest());
    }

    [Fact]
    public void Round_trips_through_the_wire_context()
    {
        var m = Sample();

        string json = JsonSerializer.Serialize(m, WireJsonContext.Default.ReleaseManifest);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.ReleaseManifest);

        Assert.Equal(m, back);
    }

    [Fact]
    public void Round_trips_through_the_json_wire_codec()
    {
        var codec = new JsonWireCodec();
        var m = Sample();

        var payload = codec.EncodePayload(m);
        var envelope = new Envelope(1, "update.manifest", EnvelopeKind.Event, null, null, null, null, null, payload);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Encode(envelope, buffer);
        var decoded = codec.DecodePayload<ReleaseManifest>(codec.Decode(buffer.WrittenSpan));

        Assert.Equal(m, decoded);
    }

    [Fact]
    public void Golden_json_is_stable()
    {
        string json = JsonSerializer.Serialize(Sample(), _indented.GetTypeInfo<ReleaseManifest>());

        GoldenFile.Check("release-manifest.json", json);
    }

    [Fact]
    public void Decoding_ignores_unknown_fields_from_a_later_minor()
    {
        string json = JsonSerializer.Serialize(Sample(), WireJsonContext.Default.ReleaseManifest)
            .Replace("{\"channel\"", "{\"future\":{\"x\":1},\"channel\"", StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.ReleaseManifest);

        Assert.Equal(Sample(), back);
    }

    [Theory]
    [InlineData("\"channel\":\"Stable\"", "\"channel\":\"Nightly\"")]
    [InlineData("\"channel\":\"Stable\"", "\"channel\":null")]
    [InlineData("\"appVersion\":\"0.1.0\"", "\"appVersion\":\"latest\"")]
    [InlineData("\"minProtocol\":\"1.0\"", "\"minProtocol\":\"1\"")]
    [InlineData("\"signingKeyFingerprint\":\"" + _fingerprint + "\"", "\"signingKeyFingerprint\":\"ABC\"")]
    [InlineData("\"url\":\"https://releases.example.org/v0.1.0/Bazaar-0.1.0-linux-x64.tar.gz\"", "\"url\":\"http://evil.example/a\"")]
    [InlineData("\"sizeBytes\":104857600", "\"sizeBytes\":-1")]
    public void Wire_decoding_refuses_invalid_values_as_a_bad_frame(string find, string replace)
    {
        var codec = new JsonWireCodec();
        string json = JsonSerializer.Serialize(Sample(), WireJsonContext.Default.ReleaseManifest);
        Assert.Contains(find, json, StringComparison.Ordinal);
        var envelope = new Envelope(
            1, "update.manifest", EnvelopeKind.Event, null, null, null, null, null,
            Encoding.UTF8.GetBytes(json.Replace(find, replace, StringComparison.Ordinal)));

        Assert.Throws<WireFormatException>(() => codec.DecodePayload<ReleaseManifest>(envelope));
    }

    [Fact]
    public void Wire_decoding_refuses_a_payload_with_missing_required_fields()
    {
        var codec = new JsonWireCodec();
        var envelope = new Envelope(
            1, "update.manifest", EnvelopeKind.Event, null, null, null, null, null,
            Encoding.UTF8.GetBytes("{\"channel\":\"Stable\"}"));

        Assert.Throws<WireFormatException>(() => codec.DecodePayload<ReleaseManifest>(envelope));
    }
}
