using Bazaar.CompanyIdentity;

namespace Bazaar.Server.Tests.Components.CompanyBuild;

public sealed class CompanyBuildUpdateTrustTests
{
    private static readonly string _pin = new('b', 64);
    private static readonly string _fingerprint40 = new('a', 40);
    private static readonly string _fingerprint64 = new('c', 64);
    private static readonly Uri _address = new("https://company.example/");
    private static readonly Uri _manifest = new("https://company.example/updates/manifest.json");

    private const string Key = "-----BEGIN PGP PUBLIC KEY BLOCK-----\n\nmQENBF\\test+key/==\n=abcd\n-----END PGP PUBLIC KEY BLOCK-----";

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Create_accepts_a_valid_fingerprint_key_and_manifest_url(int length)
    {
        var trust = UpdateTrust.Create(new string('d', length), Key, _manifest, isOwnerInstall: true);

        Assert.Equal(new string('d', length), trust.UpdateSigningKeyFingerprint);
        Assert.Equal(Key, trust.UpdateSigningPublicKey);
        Assert.Equal(_manifest, trust.ReleaseManifestUrl);
        Assert.True(trust.IsOwnerInstall);
    }

    [Fact]
    public void IsOwnerInstall_defaults_to_false()
    {
        Assert.False(UpdateTrust.Create(_fingerprint40, Key, _manifest).IsOwnerInstall);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Create_refuses_a_malformed_fingerprint(string fingerprint)
    {
        Assert.Throws<ArgumentException>(() => UpdateTrust.Create(fingerprint, Key, _manifest));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("not a key")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----\nx\n-----END PGP PRIVATE KEY BLOCK-----")]
    public void Create_refuses_an_empty_or_unarmoured_key(string key)
    {
        Assert.Throws<ArgumentException>(() => UpdateTrust.Create(_fingerprint40, key, _manifest));
    }

    [Theory]
    [InlineData("http://company.example/updates/manifest.json")]
    [InlineData("https://user:secret@company.example/updates/manifest.json")]
    public void Create_refuses_a_non_https_manifest_url(string url)
    {
        Assert.Throws<ArgumentException>(() => UpdateTrust.Create(_fingerprint40, Key, new Uri(url)));
    }

    [Fact]
    public void Create_refuses_a_relative_manifest_url()
    {
        Assert.Throws<ArgumentException>(() => UpdateTrust.Create(_fingerprint40, Key, new Uri("/updates/manifest.json", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://other.example/updates/manifest.json")]
    [InlineData("https://company.example:8443/updates/manifest.json")]
    [InlineData("https://sub.company.example/updates/manifest.json")]
    public void Server_identity_refuses_a_cross_origin_manifest_url(string url)
    {
        var trust = UpdateTrust.Create(_fingerprint40, Key, new Uri(url));

        Assert.Throws<ArgumentException>(() => CompanyServerIdentity.Create(_address, _pin, trust));
    }

    [Fact]
    public void Server_identity_accepts_a_same_origin_manifest_url()
    {
        var trust = UpdateTrust.Create(_fingerprint64, Key, _manifest);

        var identity = CompanyServerIdentity.Create(_address, _pin, trust);

        Assert.Same(trust, identity.Update);
    }

    [Fact]
    public void Existing_two_argument_create_carries_no_update_trust()
    {
        var identity = CompanyServerIdentity.Create(_address, _pin);

        Assert.Null(identity.Update);
    }

    [Fact]
    public void RenderSource_without_update_trust_renders_null_and_false()
    {
        var source = CompanyBuildIdentityGenerator.RenderSource(CompanyServerIdentity.Create(_address, _pin));

        // Generated files start in a disabled nullable context, so the annotations need the directive to compile.
        Assert.Contains("#nullable enable", source);
        Assert.Contains("UpdateSigningKeyFingerprint => null;", source);
        Assert.Contains("UpdateSigningPublicKey => null;", source);
        Assert.Contains("IsOwnerInstall => false;", source);
        Assert.Contains("ReleaseManifestUrl => null;", source);
    }

    [Fact]
    public void RenderSource_with_update_trust_writes_every_value()
    {
        var identity = CompanyServerIdentity.Create(_address, _pin, UpdateTrust.Create(_fingerprint40, Key, _manifest, isOwnerInstall: true));

        var source = CompanyBuildIdentityGenerator.RenderSource(identity);

        Assert.Contains($"UpdateSigningKeyFingerprint => \"{_fingerprint40}\";", source);
        Assert.Contains("IsOwnerInstall => true;", source);
        Assert.Contains("ReleaseManifestUrl => new Uri(\"https://company.example/updates/manifest.json\");", source);
        // The key is one string literal on one line, so the file stays valid C#; unescaping it gives the key back.
        var marker = "UpdateSigningPublicKey => \"";
        var start = source.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = source.IndexOf("\";", start, StringComparison.Ordinal);
        var literal = source[start..end];
        Assert.DoesNotContain('\n', literal);
        Assert.Equal(Key, Unescape(literal));
    }

    [Fact]
    public void WriteSource_and_WriteUpdateSigningKey_write_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-update-trust-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = CompanyServerIdentity.Create(_address, _pin, UpdateTrust.Create(_fingerprint40, Key, _manifest));

            var sourcePath = CompanyBuildIdentityGenerator.WriteSource(identity, root);
            var keyPath = CompanyBuildIdentityGenerator.WriteUpdateSigningKey(identity, root);

            Assert.Equal(CompanyBuildIdentityGenerator.RenderSource(identity), File.ReadAllText(sourcePath));
            Assert.Equal(Path.Combine(root, "UpdateSigningPublicKey.asc"), keyPath);
            Assert.Equal(Key + "\n", File.ReadAllText(keyPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteUpdateSigningKey_fails_closed_without_update_trust()
    {
        var identity = CompanyServerIdentity.Create(_address, _pin);

        Assert.Throws<InvalidOperationException>(() => CompanyBuildIdentityGenerator.WriteUpdateSigningKey(identity, Path.GetTempPath()));
    }

    private static string Unescape(string literal)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\') { builder.Append(literal[i]); continue; }
            i++;
            builder.Append(literal[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other });
        }
        return builder.ToString();
    }
}
