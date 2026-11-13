using System.Security.Cryptography;
using Bazaar.Server.Security.Erasure;

namespace Bazaar.Server.Tests.Security.Erasure;

public class ErasureProofVerifierTests
{
    private static readonly DateTimeOffset _erasedAt = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static readonly byte[] _fixedSignature = [1, 2, 3, 4];

    private static ErasureProof Proof(string signingKeyId) => ErasureProof.Create(
        SHA256.HashData([1]),
        ["key-a"],
        ["gc-messages"],
        _erasedAt.AddDays(30),
        _erasedAt,
        signingKeyId);

    /// <summary>Fake signer that always signs with a fixed signature and accepts only that signature.</summary>
    private sealed class EchoSigner(string keyId) : IErasureProofSigner
    {
        public string KeyId { get; } = keyId;

        public byte[] Sign(ReadOnlyMemory<byte> data) => _fixedSignature.ToArray();

        public bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature) =>
            signature.Span.SequenceEqual(_fixedSignature);
    }

    [Fact]
    public void Verify_accepts_a_matching_key_and_signature()
    {
        var signer = new EchoSigner("identity-key-1");

        Assert.True(ErasureProofVerifier.Verify(Proof("identity-key-1"), _fixedSignature, signer));
    }

    [Fact]
    public void Verify_rejects_a_mismatched_signing_key_id()
    {
        var signer = new EchoSigner("identity-key-2");

        Assert.False(ErasureProofVerifier.Verify(Proof("identity-key-1"), _fixedSignature, signer));
    }

    [Fact]
    public void Verify_rejects_a_signature_the_signer_does_not_accept()
    {
        var signer = new EchoSigner("identity-key-1");

        Assert.False(ErasureProofVerifier.Verify(Proof("identity-key-1"), [9, 9, 9, 9], signer));
    }

    [Fact]
    public void Verify_passes_the_proof_canonical_bytes_to_the_signer()
    {
        var proof = Proof("identity-key-1");
        var recorder = new RecordingSigner("identity-key-1");

        ErasureProofVerifier.Verify(proof, _fixedSignature, recorder);

        Assert.Equal(proof.SignedBytes(), recorder.LastVerified);
    }

    private sealed class RecordingSigner(string keyId) : IErasureProofSigner
    {
        public string KeyId { get; } = keyId;

        public byte[]? LastVerified { get; private set; }

        public byte[] Sign(ReadOnlyMemory<byte> data) => _fixedSignature.ToArray();

        public bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature)
        {
            LastVerified = data.ToArray();
            return true;
        }
    }
}
