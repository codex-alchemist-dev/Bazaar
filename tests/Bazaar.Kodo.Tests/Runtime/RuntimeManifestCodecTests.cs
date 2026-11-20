using System.Text;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Runtime;

public sealed class RuntimeManifestCodecTests
{
    [Fact]
    public void Sha256MatchesTheStandardVector()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            RuntimeManifestCodec.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void CanonicalBytesAreTheSameForTheSameManifest()
    {
        RuntimeManifest manifest = RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, RuntimeTestSigning.SampleContents());

        Assert.Equal(
            RuntimeManifestCodec.CanonicalizeManifest(manifest),
            RuntimeManifestCodec.CanonicalizeManifest(manifest));
    }

    [Fact]
    public void WhitespaceInAStoredEnvelopeDoesNotChangeTheSignedBytes()
    {
        RuntimeManifest manifest = RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, RuntimeTestSigning.SampleContents());
        byte[] compact = RuntimeTestSigning.SignedEnvelope(manifest);

        // Add spaces and line breaks between the tokens. The sample paths and hashes contain no ':' or ',' so the edit is safe.
        string spaced = Encoding.UTF8.GetString(compact).Replace("\":", "\" : ", StringComparison.Ordinal)
            .Replace(",", " ,\n  ", StringComparison.Ordinal);
        SignedRuntimeManifest? parsed = RuntimeManifestCodec.ParseEnvelope(Encoding.UTF8.GetBytes(spaced));

        Assert.NotNull(parsed);
        Assert.Equal(
            RuntimeManifestCodec.CanonicalizeManifest(manifest),
            RuntimeManifestCodec.CanonicalizeManifest(parsed.Manifest));
    }

    [Fact]
    public void CanonicalFormHasNoWhitespaceAndKeepsDeclarationOrder()
    {
        RuntimeManifest manifest = new(
            "0.2.0",
            RuntimeLayout.SlotB,
            "build-9",
            [new RuntimeFileEntry("a.bin", new string('a', 64), 3)]);

        string json = Encoding.UTF8.GetString(RuntimeManifestCodec.CanonicalizeManifest(manifest));

        Assert.Equal(
            $"{{\"version\":\"0.2.0\",\"slotId\":\"slot-b\",\"buildId\":\"build-9\",\"files\":[{{\"path\":\"a.bin\",\"sha256\":\"{new string('a', 64)}\",\"size\":3}}]}}",
            json);
    }

    [Fact]
    public void EnvelopeWithAnUnknownMemberIsRejected()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            """{"manifest":{"version":"1","slotId":"slot-a","buildId":"b","files":[],"extra":true},"keyId":"k","signature":"AA=="}""");

        Assert.Null(RuntimeManifestCodec.ParseEnvelope(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("null")]
    public void MalformedEnvelopeParsesToNull(string text)
    {
        Assert.Null(RuntimeManifestCodec.ParseEnvelope(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void PointerFileOnlyCarriesItsDeclaredMembers()
    {
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, Encoding.UTF8.GetBytes("envelope"));

        string json = Encoding.UTF8.GetString(pointer);

        Assert.DoesNotContain("body", json, StringComparison.Ordinal);
        Assert.Contains("\"slotId\":\"slot-a\"", json, StringComparison.Ordinal);
    }
}
