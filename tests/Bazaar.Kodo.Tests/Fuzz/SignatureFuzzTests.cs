using System.Text;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Tests.Export;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Fuzzing and mutation tests for the signature and hash checks (doc 46 sections 6.1, 6.3 and 15.3 and 17.4). Hostile input must
/// give a rejection status and never an exception or a pass, and one flipped bit in a signed or hashed artifact must always be
/// caught. A flipped bit that still passes is allowed only when it did not change what was signed (for example the case of a hex
/// digit that the parser reads the same), and the test checks that explicitly.
/// </summary>
public sealed class SignatureFuzzTests
{
    private static readonly Dictionary<string, string> _contents = RuntimeTestSigning.SampleContents();

    private static RuntimeSignatureChecker Checker() => new(RuntimeTestSigning.TrustRoot());

    private static byte[] Envelope(string slotId = RuntimeLayout.SlotA) =>
        RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(slotId, _contents));

    private static byte[] FlipBit(byte[] source, int byteIndex, int bit)
    {
        byte[] copy = [.. source];
        copy[byteIndex] ^= (byte)(1 << bit);
        return copy;
    }

    private static bool SamePointer(byte[] original, byte[] mutated)
    {
        SlotPointer? a = RuntimeManifestCodec.ParsePointer(original);
        SlotPointer? b = RuntimeManifestCodec.ParsePointer(mutated);
        return a is not null
            && b is not null
            && a.SlotId == b.SlotId
            && a.ManifestSha256 == b.ManifestSha256
            && a.KeyId == b.KeyId
            && SameBase64(a.Signature, b.Signature);
    }

    private static bool SameEnvelope(byte[] original, byte[] mutated)
    {
        SignedRuntimeManifest? a = RuntimeManifestCodec.ParseEnvelope(original);
        SignedRuntimeManifest? b = RuntimeManifestCodec.ParseEnvelope(mutated);
        return a?.Manifest is not null
            && b?.Manifest is not null
            && a.KeyId == b.KeyId
            && SameBase64(a.Signature, b.Signature)
            && RuntimeManifestCodec.CanonicalizeManifest(a.Manifest).AsSpan().SequenceEqual(RuntimeManifestCodec.CanonicalizeManifest(b.Manifest));
    }

    private static bool SameBase64(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a == b;
        }

        return Convert.TryFromBase64String(a, new byte[a.Length], out int first)
            && Convert.TryFromBase64String(b, new byte[b.Length], out int second)
            && first == second
            && Convert.FromBase64String(a).AsSpan().SequenceEqual(Convert.FromBase64String(b));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    public void SignatureCheckerNeverThrowsAndNeverPassesHostileInput(int seed)
    {
        RuntimeSignatureChecker checker = Checker();
        byte[] body = Encoding.UTF8.GetBytes("""{"slotId":"slot-a"}""");
        string valid = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, body);
        string[] keyIds = [RuntimeTestSigning.KeyId, "other", string.Empty, "release-test\n", "RELEASE-TEST"];

        FuzzKit.Run(
            "RuntimeSignatureChecker.Check",
            seed,
            400,
            (random, i) => BitConverter.GetBytes(random.Next()).Concat(FuzzKit.RandomBytes(random, 400)).ToArray(),
            input =>
            {
                Random random = FuzzKit.Rng(BitConverter.ToInt32(input, 0));
                string signature = (random.Next(4)) switch
                {
                    0 => Convert.ToBase64String(input),
                    1 => Encoding.Latin1.GetString(input),
                    2 => valid[..random.Next(0, valid.Length)],
                    _ => valid + Convert.ToBase64String(input[..Math.Min(4, input.Length)]),
                };

                RuntimeSignatureStatus status = checker.Check(
                    random.Next(2) == 0 ? body : input,
                    keyIds[random.Next(keyIds.Length)],
                    signature,
                    RuntimeTestSigning.Now.AddSeconds(random.Next(-5, 5)));
                Assert.NotEqual(RuntimeSignatureStatus.Valid, status);
            });
    }

    [Fact]
    public void EveryBitFlipOfASignedPointerIsRejectedOrChangesNothing()
    {
        byte[] envelope = Envelope();
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        SlotPointerVerifier verifier = new(Checker());
        Assert.Equal(SlotPointerStatus.Verified, verifier.Verify(pointer, envelope, RuntimeTestSigning.Now));

        for (int index = 0; index < pointer.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] mutated = FlipBit(pointer, index, bit);
                SlotPointerStatus status = verifier.Verify(mutated, envelope, RuntimeTestSigning.Now);
                if (status == SlotPointerStatus.Verified)
                {
                    Assert.True(SamePointer(pointer, mutated), $"pointer byte {index} bit {bit} passed and changed the signed content");
                }
            }
        }
    }

    [Fact]
    public void EveryBitFlipOfTheManifestEnvelopeIsRejectedByThePointerDigest()
    {
        byte[] envelope = Envelope();
        byte[] pointer = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        SlotPointerVerifier verifier = new(Checker());

        for (int index = 0; index < envelope.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                SlotPointerStatus status = verifier.Verify(pointer, FlipBit(envelope, index, bit), RuntimeTestSigning.Now);
                Assert.True(status != SlotPointerStatus.Verified, $"envelope byte {index} bit {bit} still verified");
            }
        }
    }

    [Theory]
    [InlineData(211)]
    [InlineData(212)]
    public void OneBitFlipOfASignedManifestNeverVerifies(int seed)
    {
        byte[] envelope = Envelope();
        RuntimeManifestVerifier verifier = new(Checker());
        Dictionary<string, ReadOnlyMemory<byte>> files = RuntimeTestSigning.ToSlotFiles(_contents);
        Assert.True(verifier.Verify(envelope, files, RuntimeTestSigning.Now).IsVerified);

        Random random = FuzzKit.Rng(seed);
        for (int index = 0; index < envelope.Length; index++)
        {
            int bit = random.Next(8);
            byte[] mutated = FlipBit(envelope, index, bit);
            RuntimeVerificationResult result = verifier.Verify(mutated, files, RuntimeTestSigning.Now);
            if (result.IsVerified)
            {
                Assert.True(SameEnvelope(envelope, mutated), $"seed={seed} envelope byte {index} bit {bit} verified and changed the signed content");
            }
        }
    }

    [Fact]
    public void OneBitFlipOfAnyListedFileIsAMismatchAndNamesTheFile()
    {
        byte[] envelope = Envelope();
        RuntimeManifestVerifier verifier = new(Checker());

        foreach (string path in _contents.Keys)
        {
            byte[] original = RuntimeTestSigning.Bytes(_contents[path]);
            for (int index = 0; index < original.Length; index++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    Dictionary<string, ReadOnlyMemory<byte>> files = RuntimeTestSigning.ToSlotFiles(_contents);
                    files[path] = FlipBit(original, index, bit);

                    RuntimeVerificationResult result = verifier.Verify(envelope, files, RuntimeTestSigning.Now);

                    Assert.False(result.IsVerified, $"{path} byte {index} bit {bit} verified");
                    Assert.Contains(result.FileProblems, problem => problem.Path == path && problem.Kind == RuntimeFileProblemKind.HashMismatch);
                }
            }
        }
    }

    [Theory]
    [InlineData(221)]
    [InlineData(222)]
    public void HostileEnvelopesNeverVerifyAndNeverThrow(int seed)
    {
        RuntimeManifestVerifier verifier = new(Checker());
        byte[] valid = Envelope();
        Dictionary<string, ReadOnlyMemory<byte>> files = RuntimeTestSigning.ToSlotFiles(_contents);

        FuzzKit.Run(
            "RuntimeManifestVerifier.Verify",
            seed,
            500,
            (random, i) => FuzzKit.Mixed(random, i, valid, ["manifest", "keyId", "signature", "slotId", "files", "path", "sha256", "size", "version", "buildId"]),
            input =>
            {
                RuntimeVerificationResult result = verifier.Verify(input, files, RuntimeTestSigning.Now);
                if (result.IsVerified)
                {
                    Assert.True(SameEnvelope(valid, input), "a changed envelope verified");
                }
            });
    }

    [Theory]
    [InlineData(231)]
    [InlineData(232)]
    public void HostilePointersNeverVerifyAndNeverThrow(int seed)
    {
        SlotPointerVerifier verifier = new(Checker());
        byte[] envelope = Envelope();
        byte[] valid = RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);

        FuzzKit.Run(
            "SlotPointerVerifier.Verify",
            seed,
            600,
            (random, i) => FuzzKit.Mixed(random, i, valid, ["slotId", "manifestSha256", "keyId", "signature", "body"]),
            input =>
            {
                SlotPointerStatus status = verifier.Verify(input, envelope, RuntimeTestSigning.Now);
                if (status == SlotPointerStatus.Verified)
                {
                    Assert.True(SamePointer(valid, input), "a changed pointer verified");
                }
            });
    }

    [Theory]
    [InlineData(241)]
    [InlineData(242)]
    public void WebhookVerifierNeverThrowsAndNeverPassesHostileHeaders(int seed)
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        byte[] body = Encoding.UTF8.GetBytes("""{"type":"kodo.alert"}""");
        KodoWebhookSignature good = KodoWebhookSigner.Sign(key, KodoExportTestData.Now, body);
        Random random = FuzzKit.Rng(seed);
        string[] timestamps = [string.Empty, "-1", "+5", " 5", "5 ", "0", "99999999999999999999", "253402300800", "253402300799", "1e3", "０１２", good.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)];

        for (int iteration = 0; iteration < 600; iteration++)
        {
            string? keyId = random.Next(5) == 0 ? null : (random.Next(2) == 0 ? "k1" : Convert.ToBase64String(FuzzKit.RandomBytes(random, 6)));
            string? timestamp = random.Next(6) == 0 ? null : timestamps[random.Next(timestamps.Length)];
            string? signature = random.Next(6) switch
            {
                0 => null,
                1 => good.Signature[..random.Next(0, good.Signature.Length)],
                2 => "v1=" + Convert.ToHexStringLower(FuzzKit.RandomBytes(random, 40)),
                3 => "v2=" + good.Signature[3..],
                4 => Convert.ToHexString(FuzzKit.RandomBytes(random, 64)),
                _ => good.Signature.ToUpperInvariant(),
            };

            KodoWebhookVerification result = KodoWebhookSigner.Verify([key], keyId, timestamp, signature, body, KodoExportTestData.Now);

            if (result == KodoWebhookVerification.Valid)
            {
                // Only the same MAC bytes may pass, for example the same hex in another case or an unchanged timestamp.
                Assert.Equal("k1", keyId);
                Assert.Equal(good.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), timestamp);
                Assert.Equal(Convert.FromHexString(good.Signature[3..]), Convert.FromHexString(signature![3..]));
            }
        }
    }

    [Fact]
    public void OneBitFlipInTheBodyTheTimestampOrTheSignatureIsNeverValid()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        byte[] body = Encoding.UTF8.GetBytes("""{"type":"kodo.alert","id":"a1","identifiers":{"probe_id":"host.disk.free"}}""");
        KodoWebhookSignature good = KodoWebhookSigner.Sign(key, KodoExportTestData.Now, body);
        string timestamp = good.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            KodoWebhookVerification.Valid,
            KodoWebhookSigner.Verify([key], good.KeyId, timestamp, good.Signature, body, KodoExportTestData.Now));

        for (int index = 0; index < body.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                KodoWebhookVerification result = KodoWebhookSigner.Verify(
                    [key], good.KeyId, timestamp, good.Signature, FlipBit(body, index, bit), KodoExportTestData.Now);
                Assert.True(result != KodoWebhookVerification.Valid, $"body byte {index} bit {bit} still valid");
            }
        }

        byte[] timestampBytes = Encoding.ASCII.GetBytes(timestamp);
        for (int index = 0; index < timestampBytes.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                string flipped = Encoding.Latin1.GetString(FlipBit(timestampBytes, index, bit));
                KodoWebhookVerification result = KodoWebhookSigner.Verify(
                    [key], good.KeyId, flipped, good.Signature, body, KodoExportTestData.Now);
                Assert.True(result != KodoWebhookVerification.Valid, $"timestamp byte {index} bit {bit} still valid");
            }
        }

        byte[] mac = Convert.FromHexString(good.Signature[3..]);
        for (int index = 0; index < mac.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                string flipped = KodoWebhookSigner.SignaturePrefix + Convert.ToHexStringLower(FlipBit(mac, index, bit));
                KodoWebhookVerification result = KodoWebhookSigner.Verify(
                    [key], good.KeyId, timestamp, flipped, body, KodoExportTestData.Now);
                Assert.True(result != KodoWebhookVerification.Valid, $"signature byte {index} bit {bit} still valid");
            }
        }
    }
}
