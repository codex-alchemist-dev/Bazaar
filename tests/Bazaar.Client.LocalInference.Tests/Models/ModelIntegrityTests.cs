using Bazaar.Client.LocalInference.Models;

namespace Bazaar.Client.LocalInference.Tests.Models;

public class ModelIntegrityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Matching_file_passes()
    {
        var manifest = ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("hello")));

        await ModelIntegrity.VerifyFilesAsync(manifest, _temp.Combine("src"), Ct);
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected()
    {
        var manifest = ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        File.WriteAllBytes(_temp.Combine("src", "a.bin"), ModelFixtures.Text("jello"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => ModelIntegrity.VerifyFilesAsync(manifest, _temp.Combine("src"), Ct));

        Assert.Equal(ModelIntegrityFailure.HashMismatch, error.Failure);
        Assert.Equal("a.bin", error.FileName);
    }

    [Fact]
    public async Task Byte_length_mismatch_is_rejected_before_hashing()
    {
        var manifest = ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        File.WriteAllBytes(_temp.Combine("src", "a.bin"), ModelFixtures.Text("hello!"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => ModelIntegrity.VerifyFilesAsync(manifest, _temp.Combine("src"), Ct));

        Assert.Equal(ModelIntegrityFailure.LengthMismatch, error.Failure);
    }

    [Fact]
    public async Task Missing_file_is_rejected()
    {
        var manifest = ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        File.Delete(_temp.Combine("src", "a.bin"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => ModelIntegrity.VerifyFilesAsync(manifest, _temp.Combine("src"), Ct));

        Assert.Equal(ModelIntegrityFailure.Missing, error.Failure);
    }

    [Fact]
    public void Unsigned_manifest_passes_when_policy_does_not_require_a_signature()
    {
        var manifest = ModelFixtures.WithSignature(
            ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("x"))), null);

        ModelIntegrity.VerifySignature(manifest, new ManifestPolicy(RequireSignature: false), FailClosedManifestVerifier.Instance);
    }

    [Fact]
    public void Unsigned_manifest_is_rejected_when_policy_requires_a_signature()
    {
        var manifest = ModelFixtures.WithSignature(
            ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("x"))), null);

        var error = Assert.Throws<ModelIntegrityException>(() =>
            ModelIntegrity.VerifySignature(manifest, new ManifestPolicy(RequireSignature: true), new FakeManifestVerifier("sig-A")));

        Assert.Equal(ModelIntegrityFailure.SignatureMissing, error.Failure);
    }

    [Fact]
    public void Signature_the_verifier_accepts_passes()
    {
        var manifest = ModelFixtures.WithSignature(
            ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("x"))), "sig-A");

        ModelIntegrity.VerifySignature(manifest, new ManifestPolicy(RequireSignature: true), new FakeManifestVerifier("sig-A"));
    }

    [Fact]
    public void Signature_the_verifier_rejects_is_rejected_even_when_not_required()
    {
        var manifest = ModelFixtures.WithSignature(
            ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("x"))), "sig-bad");

        var error = Assert.Throws<ModelIntegrityException>(() =>
            ModelIntegrity.VerifySignature(manifest, new ManifestPolicy(RequireSignature: false), new FakeManifestVerifier("sig-A")));

        Assert.Equal(ModelIntegrityFailure.SignatureInvalid, error.Failure);
    }

    [Fact]
    public void Default_verifier_rejects_every_signature()
    {
        var manifest = ModelFixtures.WithSignature(
            ModelFixtures.WriteSource(_temp.Combine("src"), "m", "1", ("a.bin", ModelFixtures.Text("x"))), "anything");

        Assert.Throws<ModelIntegrityException>(() =>
            ModelIntegrity.VerifySignature(manifest, new ManifestPolicy(RequireSignature: false), FailClosedManifestVerifier.Instance));
    }
}
