using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Runtime;

public sealed class SlotPointerVerifierTests
{
    private static readonly Dictionary<string, string> _files = RuntimeTestSigning.SampleContents();

    private static SlotPointerVerifier Verifier() =>
        new(new RuntimeSignatureChecker(RuntimeTestSigning.TrustRoot()));

    private static byte[] EnvelopeFor(string slotId, RSA? key = null) =>
        RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(slotId, _files), key);

    [Fact]
    public void SignedPointerThatNamesTheSignedManifestVerifies()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.Verified, status);
    }

    [Fact]
    public void MissingPointerIsReported()
    {
        SlotPointerStatus status = Verifier().Verify(null, EnvelopeFor(RuntimeLayout.SlotA), RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.PointerMissing, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"slotId\":\"slot-a\"}")]
    public void MalformedPointerNeverVerifies(string text)
    {
        SlotPointerStatus status = Verifier().Verify(
            Encoding.UTF8.GetBytes(text),
            EnvelopeFor(RuntimeLayout.SlotA),
            RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.Malformed, status);
    }

    [Fact]
    public void PointerWithAnExtraBodyMemberIsMalformed()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        string injected = Encoding.UTF8.GetString(pointer).Replace("}", ",\"body\":{}}", StringComparison.Ordinal);

        SlotPointerStatus status = Verifier().Verify(Encoding.UTF8.GetBytes(injected), envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.Malformed, status);
    }

    [Fact]
    public void PointerNamingAnUnknownSlotIsMalformedEvenWhenSigned()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer("slot-c", envelope);

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.Malformed, status);
    }

    [Fact]
    public void PointerChangedToAnotherSlotAfterSigningIsABadSignature()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        string flipped = Encoding.UTF8.GetString(pointer).Replace("\"slotId\":\"slot-a\"", "\"slotId\":\"slot-b\"", StringComparison.Ordinal);

        SlotPointerStatus status = Verifier().Verify(Encoding.UTF8.GetBytes(flipped), envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.BadSignature, status);
    }

    [Fact]
    public void UnsignedPointerNeverVerifies()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] unsigned = RuntimeManifestCodec.EncodePointer(new SlotPointer(
            RuntimeLayout.SlotA,
            RuntimeManifestCodec.Sha256Hex(envelope),
            RuntimeTestSigning.KeyId,
            null));

        SlotPointerStatus status = Verifier().Verify(unsigned, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.NoSignature, status);
    }

    [Fact]
    public void PointerSignedByAnUnknownKeyNeverVerifies()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope, keyId: "stranger");

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.UnknownKey, status);
    }

    [Fact]
    public void PointerSignedByTheWrongKeyNeverVerifies()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope, key: RuntimeTestSigning.OtherKey);

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.BadSignature, status);
    }

    [Fact]
    public void MissingManifestIsReportedForAValidPointer()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);

        SlotPointerStatus status = Verifier().Verify(pointer, null, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.ManifestMissing, status);
    }

    [Fact]
    public void ManifestChangedByOneByteFailsTheDigest()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotA);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        byte[] changed = [.. envelope];
        changed[^2] ^= 0x20;

        SlotPointerStatus status = Verifier().Verify(pointer, changed, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.ManifestDigestMismatch, status);
    }

    [Fact]
    public void ManifestNamingAnotherSlotIsRejectedEvenWithAMatchingDigest()
    {
        byte[] envelope = EnvelopeFor(RuntimeLayout.SlotB);
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.SlotIdMismatch, status);
    }

    [Fact]
    public void MalformedManifestWithAMatchingDigestIsNotAccepted()
    {
        byte[] envelope = Encoding.UTF8.GetBytes("{\"manifest\":null}");
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);

        SlotPointerStatus status = Verifier().Verify(pointer, envelope, RuntimeTestSigning.Now);

        Assert.Equal(SlotPointerStatus.ManifestMalformed, status);
    }

    [Fact]
    public void PreviousPointerStillVerifiesForRollbackWhenTheOtherSlotChanges()
    {
        // The previous pointer was signed when it was made. Rollback re-checks it against the old slot's manifest,
        // so it needs no signing key and no network.
        byte[] oldEnvelope = EnvelopeFor(RuntimeLayout.SlotB);
        byte[] previous = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotB, oldEnvelope);
        byte[] newEnvelope = RuntimeTestSigning.SignedEnvelope(
            RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, _files));

        SlotPointerVerifier verifier = Verifier();

        Assert.Equal(SlotPointerStatus.Verified, verifier.Verify(previous, oldEnvelope, RuntimeTestSigning.Now));
        Assert.Equal(SlotPointerStatus.ManifestDigestMismatch, verifier.Verify(previous, newEnvelope, RuntimeTestSigning.Now));
    }
}
