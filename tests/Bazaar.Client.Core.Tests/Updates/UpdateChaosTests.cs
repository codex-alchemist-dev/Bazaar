using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Updates;

namespace Bazaar.Updates.Tests;

/// <summary>
/// Update chaos (doc 37, doc 46 section 17.1): corrupt packages, bad and hostile signatures, hostile manifests,
/// downgrades by SemVer precedence. Anything offered by an untrusted feed is a refusal, never an exception.
/// </summary>
public sealed class UpdateChaosTests : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly PinnedCertificateSignatureVerifier _verifier;

    public UpdateChaosTests()
    {
        CertificateRequest request = new("CN=Chaos signing", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _verifier = new PinnedCertificateSignatureVerifier(_certificate);
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _key.Dispose();
    }

    private static Uri Https => new("https://company.example/updates/Bazaar.msix");

    private UpdateManifest Manifest(byte[] package) =>
        new("0.2.0", Https, Convert.ToHexString(SHA256.HashData(package)), _key.SignData(package, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    [Fact]
    public void AnIntactPackageVerifies()
    {
        byte[] package = new byte[4096];
        RandomNumberGenerator.Fill(package);

        Assert.True(UpdateVerifier.Verify(Manifest(package), package, _verifier));
    }

    [Fact]
    public void EveryTruncationAndEveryByteFlipOfThePackageIsRefused()
    {
        byte[] package = new byte[512];
        RandomNumberGenerator.Fill(package);
        UpdateManifest manifest = Manifest(package);

        for (int length = 0; length < package.Length; length++)
        {
            Assert.False(UpdateVerifier.Verify(manifest, package.AsSpan(0, length), _verifier));
        }

        for (int offset = 0; offset < package.Length; offset++)
        {
            byte[] mutated = [.. package];
            mutated[offset] ^= 0x01;
            Assert.False(UpdateVerifier.Verify(manifest, mutated, _verifier));
        }

        Assert.False(UpdateVerifier.Verify(manifest, [.. package, 0], _verifier));
    }

    [Fact]
    public void ARepackagedBodyWithAValidHashButTheOldSignatureIsRefused()
    {
        // The attacker controls the feed: new bytes, a matching new hash, the signature of the old package.
        byte[] original = new byte[256];
        RandomNumberGenerator.Fill(original);
        byte[] evil = [.. original];
        evil[0] ^= 0xFF;
        UpdateManifest forged = Manifest(original) with { Sha256Hex = Convert.ToHexString(SHA256.HashData(evil)) };

        Assert.False(UpdateVerifier.Verify(forged, evil, _verifier));
    }

    [Fact]
    public void HostileSignaturesAndHashesAreRefusedNotThrown()
    {
        byte[] package = new byte[64];
        UpdateManifest good = Manifest(package);

        byte[][] signatures = [[], new byte[1], new byte[255], new byte[256], new byte[257], new byte[1_000_000], [.. good.Signature, 0], good.Signature[..255]];
        foreach (byte[] signature in signatures)
        {
            Assert.False(UpdateVerifier.Verify(good with { Signature = signature }, package, _verifier));
        }

        Assert.False(UpdateVerifier.Verify(good with { Signature = null! }, package, _verifier));

        string[] hashes = ["", "zz", new string('0', 64), new string('G', 64), new string('a', 63), new string('a', 66), good.Sha256Hex + "\n", good.Sha256Hex.ToLowerInvariant()[..^1] + "g"];
        foreach (string hash in hashes)
        {
            Assert.False(UpdateVerifier.Verify(good with { Sha256Hex = hash }, package, _verifier));
        }

        Assert.False(UpdateVerifier.Verify(good with { Sha256Hex = null! }, package, _verifier));
    }

    [Fact]
    public void AnotherCompanysSignatureIsRefused()
    {
        using RSA other = RSA.Create(2048);
        byte[] package = new byte[128];
        UpdateManifest manifest = Manifest(package) with { Signature = other.SignData(package, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) };

        Assert.False(UpdateVerifier.Verify(manifest, package, _verifier));
    }

    [Theory]
    [InlineData("0.1.0-rc.10", "0.1.0-rc.2")]
    [InlineData("0.1.0-beta.11", "0.1.0-beta.9")]
    [InlineData("0.1.0-alpha.1.10", "0.1.0-alpha.1.2")]
    public void ANumericPreReleaseCounterIsComparedAsANumberSoItCannotBeDowngraded(string installed, string offered)
    {
        // SemVer 2.0 section 11: numeric identifiers compare as numbers, so rc.2 is OLDER than rc.10 and must be refused.
        UpdateDecision decision = UpdateDecision.Decide(UpdateVersion.Parse(installed), new UpdateManifest(offered, Https, new string('a', 64), [1]));

        Assert.False(decision.Apply);
        Assert.Equal(UpdateRefusal.NotNewer, decision.Refusal);
    }

    [Theory]
    [InlineData("0.1.0-rc.2", "0.1.0-rc.10")]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.1.1")]
    [InlineData("0.1.0-1", "0.1.0-alpha")]
    [InlineData("0.1.0-rc.1", "0.1.0")]
    public void ANewerPreReleaseIsOffered(string installed, string offered)
    {
        UpdateDecision decision = UpdateDecision.Decide(UpdateVersion.Parse(installed), new UpdateManifest(offered, Https, new string('a', 64), [1]));

        Assert.True(decision.Apply);
    }

    [Theory]
    [InlineData("0.01.0")]
    [InlineData("00.1.0")]
    [InlineData("0.1.0-01")]
    [InlineData("0.1.0-a..b")]
    [InlineData("0.1.0-a b")]
    [InlineData("0.1.0\n")]
    [InlineData("99999999999.0.0")]
    [InlineData("0.1.0-é")]
    public void VersionsThatAreNotSemVerAreInvalidNotCrashes(string text)
    {
        UpdateDecision decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), new UpdateManifest(text, Https, new string('a', 64), [1]));

        Assert.False(decision.Apply);
    }

    [Fact]
    public void HostilePackageUrlsAreRefusedNotThrown()
    {
        UpdateVersion installed = UpdateVersion.Parse("0.1.0");
        Uri[] urls =
        [
            new("http://company.example/x.msix"),
            new("http://localhost.evil.example/x.msix"),
            new("http://user@evil.example/x.msix"),
            new("ftp://company.example/x.msix"),
            new("file:///etc/passwd"),
            new("relative/path.msix", UriKind.Relative),
        ];

        foreach (Uri url in urls)
        {
            UpdateDecision decision = UpdateDecision.Decide(installed, new UpdateManifest("0.2.0", url, new string('a', 64), [1]));
            Assert.False(decision.Apply);
            Assert.Equal(UpdateRefusal.InsecureUrl, decision.Refusal);
        }

        Assert.True(UpdateDecision.Decide(installed, new UpdateManifest("0.2.0", new Uri("http://127.0.0.1:5000/x.msix"), new string('a', 64), [1])).Apply);
    }

    [Fact]
    public void ARollbackOfARollbackNeverMovesTheInstalledVersionBackwards()
    {
        // Installed 0.3.0. A feed replays older manifests in a loop (0.2.0, 0.1.0, 0.2.0 ...): none may apply.
        UpdateVersion installed = UpdateVersion.Parse("0.3.0");
        foreach (string offered in new[] { "0.2.0", "0.1.0", "0.2.0", "0.3.0", "0.3.0+other-commit", "0.3.0-rc.1" })
        {
            Assert.False(UpdateDecision.Decide(installed, new UpdateManifest(offered, Https, new string('a', 64), [1])).Apply);
        }
    }
}
