using System.Security.Cryptography;
using Bazaar.Updates;

namespace Bazaar.Updates.Tests;

public sealed class UpdateTests
{
    private static Uri Https => new("https://company.example/updates/Bazaar.msix");

    [Theory]
    [InlineData("0.1.0", 0, 1, 0, "")]
    [InlineData("0.1.0+a1b2c3d", 0, 1, 0, "")]
    [InlineData("1.12.3-beta.2", 1, 12, 3, "beta.2")]
    public void TryParse_accepts_bazaar_versions(string text, int major, int minor, int patch, string preRelease)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3-")]
    [InlineData("-1.2.3")]
    public void TryParse_rejects_malformed_versions(string text)
    {
        Assert.False(UpdateVersion.TryParse(text, out _));
    }

    [Fact]
    public void Build_metadata_does_not_change_precedence()
    {
        var withCommit = UpdateVersion.Parse("0.1.0+a1b2c3d");
        var plain = UpdateVersion.Parse("0.1.0");

        Assert.Equal(0, withCommit.CompareTo(plain));
    }

    [Fact]
    public void A_release_outranks_its_own_prerelease()
    {
        Assert.True(UpdateVersion.Parse("0.2.0") > UpdateVersion.Parse("0.2.0-beta.1"));
    }

    [Fact]
    public void Ordering_follows_major_minor_then_patch()
    {
        Assert.True(UpdateVersion.Parse("0.10.0") > UpdateVersion.Parse("0.9.9"));
        Assert.True(UpdateVersion.Parse("1.0.0") > UpdateVersion.Parse("0.99.99"));
        Assert.True(UpdateVersion.Parse("0.1.2") > UpdateVersion.Parse("0.1.1"));
    }

    [Fact]
    public void Newer_release_is_applied()
    {
        var manifest = new UpdateManifest("0.2.0", Https, new string('a', 64), [1]);

        var decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), manifest);

        Assert.True(decision.Apply);
        Assert.Null(decision.Refusal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0+a1b2c3d")]
    [InlineData("0.0.9")]
    public void Same_or_older_release_is_refused_as_not_newer(string offered)
    {
        var manifest = new UpdateManifest(offered, Https, new string('a', 64), [1]);

        var decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), manifest);

        Assert.False(decision.Apply);
        Assert.Equal(UpdateRefusal.NotNewer, decision.Refusal);
    }

    [Fact]
    public void Invalid_version_string_is_refused()
    {
        var manifest = new UpdateManifest("not-a-version", Https, new string('a', 64), [1]);

        var decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), manifest);

        Assert.False(decision.Apply);
        Assert.Equal(UpdateRefusal.InvalidVersion, decision.Refusal);
    }

    [Fact]
    public void Plain_http_to_a_remote_host_is_refused()
    {
        var manifest = new UpdateManifest("0.2.0", new Uri("http://company.example/Bazaar.msix"), new string('a', 64), [1]);

        var decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), manifest);

        Assert.False(decision.Apply);
        Assert.Equal(UpdateRefusal.InsecureUrl, decision.Refusal);
    }

    [Fact]
    public void Plain_http_on_loopback_is_allowed_for_local_testing()
    {
        var manifest = new UpdateManifest("0.2.0", new Uri("http://127.0.0.1:8080/Bazaar.msix"), new string('a', 64), [1]);

        var decision = UpdateDecision.Decide(UpdateVersion.Parse("0.1.0"), manifest);

        Assert.True(decision.Apply);
    }

    [Fact]
    public void Matching_hash_and_valid_signature_verify()
    {
        var package = "package bytes"u8.ToArray();
        var manifest = new UpdateManifest("0.2.0", Https, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), [9]);

        Assert.True(UpdateVerifier.Verify(manifest, package, new FakeVerifier(valid: true)));
    }

    [Fact]
    public void Tampered_package_fails_the_hash_check_before_the_signature_is_tried()
    {
        var package = "package bytes"u8.ToArray();
        var manifest = new UpdateManifest("0.2.0", Https, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), [9]);
        var verifier = new FakeVerifier(valid: true);

        var ok = UpdateVerifier.Verify(manifest, "tampered"u8, verifier);

        Assert.False(ok);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public void Bad_signature_fails_even_when_the_hash_matches()
    {
        var package = "package bytes"u8.ToArray();
        var manifest = new UpdateManifest("0.2.0", Https, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), [9]);

        Assert.False(UpdateVerifier.Verify(manifest, package, new FakeVerifier(valid: false)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("abcd")]
    public void Malformed_hash_fails_closed(string hash)
    {
        var manifest = new UpdateManifest("0.2.0", Https, hash, [9]);

        Assert.False(UpdateVerifier.Verify(manifest, "x"u8, new FakeVerifier(valid: true)));
    }

    private sealed class FakeVerifier(bool valid) : IUpdateSignatureVerifier
    {
        public int Calls { get; private set; }

        public bool Verify(ReadOnlySpan<byte> package, ReadOnlySpan<byte> signature)
        {
            Calls++;
            return valid;
        }
    }
}
