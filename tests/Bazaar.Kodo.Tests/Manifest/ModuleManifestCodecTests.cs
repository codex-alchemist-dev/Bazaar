using System.Text;
using Bazaar.Kodo.Manifest;

namespace Bazaar.Kodo.Tests.Manifest;

public sealed class ModuleManifestCodecTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void EncodedManifestRoundTrips()
    {
        ModuleManifest manifest = ManifestTestData.Sign(ManifestTestData.Valid());

        ModuleManifestDecoding decoded = ModuleManifestCodec.Decode(ModuleManifestCodec.Encode(manifest));

        Assert.Null(decoded.Cause);
        Assert.Equal(manifest.Id, decoded.Manifest!.Id);
        Assert.Equal(manifest.Probes, decoded.Manifest.Probes);
        Assert.Equal(manifest.Signature, decoded.Manifest.Signature);
        Assert.Equal(1, decoded.Manifest.KodoContract);
    }

    [Fact]
    public void SketchShapeFromTheDesignDocDecodes()
    {
        const string json = """
            {"id":"gc","name":"GC","kind":"first-party","version":"0.1.0+a1b2c3d","kodo.contract":1,
             "dependencies":{"hard":["audit"],"soft":["kanban"]},
             "health":{"liveness":"builtin","readiness":"builtin","selfTest":{"quickBudgetMs":2000,"fullBudgetMs":30000,"scheduled":"daily"}},
             "probes":["gc.notify_listener"],"runbooks":["runbooks/gc-notify-restart.json"],
             "permissions":["db.read:gc","kodo.observe"],
             "signature":{"keyId":"release-2026","alg":"Ed25519","value":"abc"}}
            """;

        ModuleManifestDecoding decoded = ModuleManifestCodec.Decode(Utf8(json));

        Assert.Null(decoded.Cause);
        Assert.Equal("release-2026", decoded.Manifest!.Signature!.KeyId);
    }

    [Fact]
    public void CanonicalBytesLeaveOutTheSignatureAndAreStable()
    {
        ModuleManifest unsigned = ManifestTestData.Valid();
        ModuleManifest signed = ManifestTestData.Sign(unsigned);

        byte[] a = ModuleManifestCodec.CanonicalBytes(unsigned);
        byte[] b = ModuleManifestCodec.CanonicalBytes(signed);

        Assert.Equal(a, b);
        Assert.DoesNotContain("signature", Encoding.UTF8.GetString(a), StringComparison.Ordinal);
        Assert.Contains("\"kodo.contract\":1", Encoding.UTF8.GetString(a), StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedInputIsRefusedBeforeParsing()
    {
        byte[] bytes = new byte[ModuleManifestCodec.MaxBytes + 1];

        Assert.Equal(ModuleManifestCauses.TooLarge, ModuleManifestCodec.Decode(bytes).Cause);
    }

    [Fact]
    public void TooDeepNestingIsRefused()
    {
        string json = new string('[', ModuleManifestCodec.MaxDepth + 1) + new string(']', ModuleManifestCodec.MaxDepth + 1);

        Assert.Equal(ModuleManifestCauses.TooDeep, ModuleManifestCodec.Decode(Utf8(json)).Cause);
    }

    [Fact]
    public void DuplicateFieldIsRefused()
    {
        string json = """{"id":"gc","id":"evil"}""";

        Assert.Equal(ModuleManifestCauses.DuplicateField, ModuleManifestCodec.Decode(Utf8(json)).Cause);
    }

    [Fact]
    public void DuplicateFieldInANestedObjectIsRefused()
    {
        string json = """{"health":{"liveness":"builtin","liveness":"module"}}""";

        Assert.Equal(ModuleManifestCauses.DuplicateField, ModuleManifestCodec.Decode(Utf8(json)).Cause);
    }

    [Fact]
    public void SameNameInSiblingObjectsIsNotADuplicate()
    {
        string json = """{"dependencies":{"hard":["a"]},"health":{"selfTest":{"scheduled":"off"}},"signature":{"value":"x"}}""";

        Assert.Null(ModuleManifestCodec.Decode(Utf8(json)).Cause);
    }

    [Theory]
    [InlineData("""{"id":"gc","extra":1}""")]
    [InlineData("""{"health":{"liveness":"builtin","bogus":true}}""")]
    [InlineData("""{"id":5}""")]
    [InlineData("""{"id":"gc"} trailing""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"id":"gc",}""")]
    public void BadShapesAreMalformed(string json)
    {
        Assert.Equal(ModuleManifestCauses.Malformed, ModuleManifestCodec.Decode(Utf8(json)).Cause);
    }

    [Fact]
    public void CommentsAreMalformed()
    {
        Assert.Equal(ModuleManifestCauses.Malformed, ModuleManifestCodec.Decode(Utf8("""{"id":"gc"} // note""")).Cause);
    }
}
