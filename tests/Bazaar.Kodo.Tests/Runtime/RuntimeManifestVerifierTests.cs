using System.Text;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Runtime;

public sealed class RuntimeManifestVerifierTests
{
    private static RuntimeManifestVerifier Verifier() =>
        new(new RuntimeSignatureChecker(RuntimeTestSigning.TrustRoot()));

    private static RuntimeVerificationResult Verify(byte[]? envelope, Dictionary<string, string> files) =>
        Verifier().Verify(envelope, RuntimeTestSigning.ToSlotFiles(files), RuntimeTestSigning.Now);

    [Fact]
    public void CleanSignedSlotVerifies()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.True(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.Verified, result.Status);
        Assert.Empty(result.FileProblems);
    }

    [Fact]
    public void MissingManifestNeverVerifies()
    {
        RuntimeVerificationResult result = Verify(null, RuntimeTestSigning.SampleContents());

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.Missing, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an envelope")]
    [InlineData("{\"manifest\":null,\"keyId\":\"release-test\",\"signature\":\"AA==\"}")]
    public void MalformedManifestNeverVerifies(string text)
    {
        RuntimeVerificationResult result = Verify(Encoding.UTF8.GetBytes(text), RuntimeTestSigning.SampleContents());

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Fact]
    public void UnsignedManifestNeverVerifiesEvenWhenEveryFileMatches()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        RuntimeManifest manifest = RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files);
        byte[] unsigned = RuntimeManifestCodec.EncodeEnvelope(new SignedRuntimeManifest(manifest, RuntimeTestSigning.KeyId, null));

        RuntimeVerificationResult result = Verify(unsigned, files);

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.NoSignature, result.Status);
    }

    [Fact]
    public void ManifestSignedByAnUnknownKeyNeverVerifies()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(
            RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files),
            keyId: "stranger");

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.UnknownKey, result.Status);
    }

    [Fact]
    public void ManifestSignedWithTheWrongKeyNeverVerifies()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(
            RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files),
            key: RuntimeTestSigning.OtherKey);

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.BadSignature, result.Status);
    }

    [Fact]
    public void ManifestTamperedAfterSigningNeverVerifiesEvenWithCleanFiles()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));
        byte[] tampered = ReplaceOnce(envelope, "\"version\":\"0.1.0\"", "\"version\":\"9.9.9\"");

        RuntimeVerificationResult result = Verify(tampered, files);

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.BadSignature, result.Status);
    }

    [Fact]
    public void ManifestWithADigestChangedAfterSigningNeverVerifies()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        RuntimeManifest signedManifest = RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files);
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(signedManifest);
        string original = signedManifest.Files[0].Sha256;
        string forged = new(original.Reverse().ToArray());
        byte[] tampered = ReplaceOnce(envelope, original, forged);

        RuntimeVerificationResult result = Verify(tampered, files);

        Assert.False(result.IsVerified);
        Assert.Equal(RuntimeManifestStatus.BadSignature, result.Status);
    }

    [Fact]
    public void ChangedFileOfAnotherSizeIsListedAsASizeMismatch()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));
        files["probes/catalogue.json"] = "{\"x\":1}";

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.Equal(RuntimeManifestStatus.FileMismatch, result.Status);
        RuntimeFileProblem problem = Assert.Single(result.FileProblems);
        Assert.Equal("probes/catalogue.json", problem.Path);
        Assert.Equal(RuntimeFileProblemKind.SizeMismatch, problem.Kind);
    }

    [Fact]
    public void SameSizeChangeIsAHashMismatchWithBothHashes()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        RuntimeManifest manifest = RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files);
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(manifest);
        files["kodo.dll"] = "binary-b";

        RuntimeVerificationResult result = Verify(envelope, files);

        RuntimeFileProblem problem = Assert.Single(result.FileProblems);
        Assert.Equal(RuntimeFileProblemKind.HashMismatch, problem.Kind);
        Assert.Equal(manifest.Files.Single(f => f.Path == "kodo.dll").Sha256, problem.ExpectedSha256);
        Assert.Equal(RuntimeManifestCodec.Sha256Hex(Encoding.UTF8.GetBytes("binary-b")), problem.ActualSha256);
    }

    [Fact]
    public void EveryMismatchIsListedNotJustTheFirst()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));
        files["kodo.dll"] = "binary-b";
        files.Remove("runbooks/restart.md");
        files["extra/dropped.dll"] = "planted";
        files["probes/catalogue.json"] = "{\"changed\":true}";

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.Equal(RuntimeManifestStatus.FileMismatch, result.Status);
        Assert.Equal(4, result.FileProblems.Count);
        Assert.Contains(result.FileProblems, p => p.Path == "kodo.dll" && p.Kind == RuntimeFileProblemKind.HashMismatch);
        Assert.Contains(result.FileProblems, p => p.Path == "probes/catalogue.json" && p.Kind == RuntimeFileProblemKind.SizeMismatch);
        Assert.Contains(result.FileProblems, p => p.Path == "runbooks/restart.md" && p.Kind == RuntimeFileProblemKind.Missing);
        Assert.Contains(result.FileProblems, p => p.Path == "extra/dropped.dll" && p.Kind == RuntimeFileProblemKind.Unlisted);
    }

    [Fact]
    public void AFileAddedToTheSlotIsUnlisted()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotB, files));
        files["plugin.dll"] = "injected";

        RuntimeVerificationResult result = Verify(envelope, files);

        RuntimeFileProblem problem = Assert.Single(result.FileProblems);
        Assert.Equal(RuntimeFileProblemKind.Unlisted, problem.Kind);
        Assert.Equal("plugin.dll", problem.Path);
        Assert.Equal(RuntimeManifestStatus.FileMismatch, result.Status);
    }

    [Fact]
    public void FileProblemsAreStillListedWhenTheSignatureFails()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(
            RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files),
            key: RuntimeTestSigning.OtherKey);
        files["kodo.dll"] = "binary-b";

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.Equal(RuntimeManifestStatus.BadSignature, result.Status);
        Assert.Single(result.FileProblems);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("nested\\back.dll")]
    [InlineData("C:drive.dll")]
    [InlineData("dir//empty.dll")]
    [InlineData("dir/./self.dll")]
    [InlineData("")]
    public void SignedManifestWithAnUnsafePathIsMalformed(string path)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal) { [path] = "x" };
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));

        RuntimeVerificationResult result = Verify(envelope, files);

        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Fact]
    public void SignedManifestWithUnsortedFilesIsMalformed()
    {
        RuntimeManifest manifest = new(
            "0.1.0",
            RuntimeLayout.SlotA,
            "build-1",
            [
                new RuntimeFileEntry("b.dll", Hash("b"), 1),
                new RuntimeFileEntry("a.dll", Hash("a"), 1),
            ]);
        Dictionary<string, string> files = new(StringComparer.Ordinal) { ["a.dll"] = "a", ["b.dll"] = "b" };

        RuntimeVerificationResult result = Verify(RuntimeTestSigning.SignedEnvelope(manifest), files);

        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Fact]
    public void SignedManifestWithPathsThatDifferOnlyByCaseIsMalformed()
    {
        RuntimeManifest manifest = new(
            "0.1.0",
            RuntimeLayout.SlotA,
            "build-1",
            [
                new RuntimeFileEntry("Kodo.dll", Hash("a"), 1),
                new RuntimeFileEntry("kodo.dll", Hash("b"), 1),
            ]);
        Dictionary<string, string> files = new(StringComparer.Ordinal) { ["Kodo.dll"] = "a", ["kodo.dll"] = "b" };

        RuntimeVerificationResult result = Verify(RuntimeTestSigning.SignedEnvelope(manifest), files);

        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("slot-c")]
    [InlineData("")]
    public void SignedManifestWithAnUnknownSlotIdIsMalformed(string slotId)
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        RuntimeManifest manifest = RuntimeTestSigning.ManifestFor(slotId, files);

        RuntimeVerificationResult result = Verify(RuntimeTestSigning.SignedEnvelope(manifest), files);

        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Fact]
    public void SignedManifestWithUppercaseHashIsMalformed()
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal) { ["a.dll"] = "a" };
        RuntimeManifest manifest = new(
            "0.1.0",
            RuntimeLayout.SlotA,
            "build-1",
            [new RuntimeFileEntry("a.dll", Hash("a").ToUpperInvariant(), 1)]);

        RuntimeVerificationResult result = Verify(RuntimeTestSigning.SignedEnvelope(manifest), files);

        Assert.Equal(RuntimeManifestStatus.Malformed, result.Status);
    }

    [Fact]
    public void ListedFileThatIsGoneIsMissing()
    {
        Dictionary<string, string> files = RuntimeTestSigning.SampleContents();
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, files));
        files.Remove("kodo.dll");

        RuntimeVerificationResult result = Verify(envelope, files);

        RuntimeFileProblem problem = Assert.Single(result.FileProblems);
        Assert.Equal(RuntimeFileProblemKind.Missing, problem.Kind);
        Assert.Equal("kodo.dll", problem.Path);
    }

    private static string Hash(string text) => RuntimeManifestCodec.Sha256Hex(Encoding.UTF8.GetBytes(text));

    private static byte[] ReplaceOnce(byte[] bytes, string search, string replacement)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int index = text.IndexOf(search, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{search}' was not found in the envelope.");
        return Encoding.UTF8.GetBytes(text.Remove(index, search.Length).Insert(index, replacement));
    }
}
