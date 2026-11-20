using System.Text.Json;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Releases;

/// <summary>Linux and Windows pairings of os, architecture and format, and the two trust paths (doc 30 section 7, doc 37 section 4b).</summary>
public class ReleaseArtifactOsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef01234567";
    private const string Https = "https://company.example.org/updates/pkg";

    // 256 zero bytes, a plausible RSA-2048 signature length.
    private static readonly string _signature = Convert.ToBase64String(new byte[256]);

    private static ReleaseArtifact Linux(string arch = "linux-x64", string format = "tar.gz", string? shaUrl = Https + ".sha256", string? sigUrl = Https + ".asc", string? sig = null) =>
        new("linux", arch, format, Https, 1000, Sha, shaUrl, sigUrl, sig);

    private static ReleaseArtifact Windows(string arch = "windows-x64", string format = "velopack", string? shaUrl = null, string? sigUrl = null, string? sig = "") =>
        new("windows", arch, format, Https, 1000, Sha, shaUrl, sigUrl, sig == "" ? _signature : sig);

    public static TheoryData<string, string, string, bool> Pairings()
    {
        var data = new TheoryData<string, string, string, bool>();
        string[] oses = ["linux", "windows"];
        string[] archs = ["linux-x64", "linux-arm64", "windows-x64", "windows-arm64"];
        string[] formats = ["tar.gz", "appimage", "msix", "velopack"];
        foreach (var os in oses)
        {
            foreach (var arch in archs)
            {
                foreach (var format in formats)
                {
                    bool archOk = arch.StartsWith(os + "-", StringComparison.Ordinal);
                    bool formatOk = os == "linux" ? format is "tar.gz" or "appimage" : format is "msix" or "velopack";
                    data.Add(os, arch, format, archOk && formatOk);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairings))]
    public void Every_os_architecture_and_format_pairing_is_accepted_only_when_it_belongs_together(
        string os, string arch, string format, bool valid)
    {
        ReleaseArtifact Make() => os == "linux"
            ? new ReleaseArtifact(os, arch, format, Https, 1000, Sha, Https + ".sha256", Https + ".asc")
            : new ReleaseArtifact(os, arch, format, Https, 1000, Sha, null, null, _signature);

        if (valid) Assert.Equal(arch, Make().Architecture);
        else Assert.Throws<ArgumentException>(Make);
    }

    [Theory]
    [InlineData("macos", "macos-arm64", "tar.gz")]
    [InlineData("windows", "win-x64", "msix")]
    [InlineData("windows", "windows-x86", "msix")]
    [InlineData("windows", "windows-x64", "exe")]
    [InlineData("Windows", "windows-x64", "msix")]
    [InlineData("linux", "linux-x64", "deb")]
    public void Unknown_values_are_refused(string os, string arch, string format) =>
        Assert.Throws<ArgumentException>(() => new ReleaseArtifact(os, arch, format, Https, 1000, Sha, Https + ".sha256", Https + ".asc", _signature));

    [Fact]
    public void Linux_requires_both_urls_and_forbids_an_inline_signature()
    {
        Assert.Throws<ArgumentException>(() => Linux(shaUrl: null));
        Assert.Throws<ArgumentException>(() => Linux(sigUrl: null));
        Assert.Throws<ArgumentException>(() => Linux(sig: _signature));
    }

    [Fact]
    public void Windows_requires_an_inline_signature_and_makes_both_urls_optional()
    {
        var bare = Windows();
        Assert.Null(bare.Sha256Url);
        Assert.Null(bare.SignatureUrl);
        Assert.Equal(_signature, bare.Signature);

        var withUrls = Windows(shaUrl: Https + ".sha256", sigUrl: Https + ".sig");
        Assert.Equal(Https + ".sig", withUrls.SignatureUrl);

        Assert.Throws<ArgumentException>(() => Windows(sig: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("not base64!!")]
    [InlineData("AAAA AAAA")]
    public void Windows_inline_signature_must_be_padded_base64(string signature)
    {
        Assert.Throws<ArgumentException>(() => new ReleaseArtifact("windows", "windows-x64", "msix", Https, 1000, Sha, null, null, signature));
    }

    [Fact]
    public void Windows_inline_signature_is_length_bounded()
    {
        string tooLong = new('A', ReleaseArtifact.MaxSignatureLength + 4);
        Assert.Throws<ArgumentException>(() => new ReleaseArtifact("windows", "windows-x64", "msix", Https, 1000, Sha, null, null, tooLong));
    }

    [Fact]
    public void Optional_windows_urls_are_still_validated_when_present()
    {
        Assert.Throws<ArgumentException>(() => Windows(shaUrl: "http://evil.example/a"));
        Assert.Throws<ArgumentException>(() => Windows(sigUrl: "../a.sig"));
    }

    [Fact]
    public void A_manifest_serves_one_os_and_a_windows_manifest_round_trips_without_null_fields()
    {
        var mixed = new[] { Linux(), Windows() };
        Assert.Throws<ArgumentException>(() => new ReleaseManifest("Stable", "0.1.0", "1.0", "1.0", "7", Fingerprint, mixed));

        var m = new ReleaseManifest("Stable", "0.1.0", "1.0", "1.0", "7", Fingerprint,
            [Windows(), Windows(arch: "windows-arm64", format: "msix")]);

        string json = JsonSerializer.Serialize(m, WireJsonContext.Default.ReleaseManifest);
        Assert.DoesNotContain("signatureUrl", json, StringComparison.Ordinal);
        Assert.Contains("\"signature\":", json, StringComparison.Ordinal);
        Assert.Equal(m, JsonSerializer.Deserialize(json, WireJsonContext.Default.ReleaseManifest));
    }

    [Fact]
    public void A_windows_wire_artifact_without_a_signature_is_refused()
    {
        string json = "{\"channel\":\"Stable\",\"appVersion\":\"0.1.0\",\"minProtocol\":\"1.0\",\"maxProtocol\":\"1.0\"," +
            "\"schemaVersion\":\"7\",\"signingKeyFingerprint\":\"" + Fingerprint + "\",\"artifacts\":[{\"os\":\"windows\"," +
            "\"architecture\":\"windows-x64\",\"format\":\"msix\",\"url\":\"" + Https + "\",\"sizeBytes\":10,\"sha256\":\"" + Sha + "\"}]}";

        Assert.ThrowsAny<ArgumentException>(() => JsonSerializer.Deserialize(json, WireJsonContext.Default.ReleaseManifest));
    }
}
