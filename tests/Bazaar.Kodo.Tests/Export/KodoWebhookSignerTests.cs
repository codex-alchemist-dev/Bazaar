using Bazaar.Contracts.Kodo;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoWebhookSignerTests
{
    private static readonly DateTimeOffset _now = KodoExportTestData.Now;
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("{\"type\":\"kodo.alert\",\"id\":\"evt_1\"}");

    [Fact]
    public void SignedBodyVerifiesWithItsOwnKey()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);

        KodoWebhookVerification result = Verify(key, signature, _body, _now);

        Assert.Equal(KodoWebhookVerification.Valid, result);
    }

    [Fact]
    public void SignatureIsHmacSha256OverTimestampDotBody()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(KodoWebhookSigningKey.MinimumSecretLength);
        KodoWebhookSigningKey key = new("k1", secret);

        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);

        byte[] signed = Encoding.ASCII.GetBytes(signature.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".")
            .Concat(_body)
            .ToArray();
        string expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, signed));
        Assert.Equal(expected, signature.Signature);
        Assert.Equal("k1", signature.KeyId);
        Assert.Equal(_now.ToUnixTimeSeconds(), signature.Timestamp);
    }

    [Fact]
    public void ChangedBodyFailsVerification()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);
        byte[] changed = [.. _body, (byte)'\n'];

        Assert.Equal(KodoWebhookVerification.BadSignature, Verify(key, signature, changed, _now));
    }

    [Fact]
    public void ChangedTimestampFailsVerification()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);
        KodoWebhookSignature moved = signature with { Timestamp = signature.Timestamp + 1 };

        Assert.Equal(KodoWebhookVerification.BadSignature, Verify(key, moved, _body, _now));
    }

    [Fact]
    public void AnotherSecretWithTheSameKeyIdFailsVerification()
    {
        KodoWebhookSigningKey signer = KodoExportTestData.NewKey("k1");
        KodoWebhookSigningKey other = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(signer, _now, _body);

        Assert.Equal(KodoWebhookVerification.BadSignature, Verify(other, signature, _body, _now));
    }

    [Fact]
    public void AKeyIdThatIsNotSuppliedIsUnknown()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);
        KodoWebhookSignature other = signature with { KeyId = "k2" };

        Assert.Equal(KodoWebhookVerification.UnknownKey, Verify(key, other, _body, _now));
    }

    [Fact]
    public void BothKeysAreAcceptedDuringRotation()
    {
        KodoWebhookSigningKey oldKey = KodoExportTestData.NewKey("old");
        KodoWebhookSigningKey newKey = KodoExportTestData.NewKey("new");
        KodoWebhookSignature fromOld = KodoWebhookSigner.Sign(oldKey, _now, _body);
        KodoWebhookSignature fromNew = KodoWebhookSigner.Sign(newKey, _now, _body);

        Assert.Equal(KodoWebhookVerification.Valid, Verify([oldKey, newKey], fromOld, _body, _now));
        Assert.Equal(KodoWebhookVerification.Valid, Verify([oldKey, newKey], fromNew, _body, _now));
        Assert.Equal(KodoWebhookVerification.UnknownKey, Verify([newKey], fromOld, _body, _now));
    }

    [Theory]
    [InlineData(0, KodoWebhookVerification.Valid)]
    [InlineData(300, KodoWebhookVerification.Valid)]
    [InlineData(-300, KodoWebhookVerification.Valid)]
    [InlineData(301, KodoWebhookVerification.OutsideReplayWindow)]
    [InlineData(-301, KodoWebhookVerification.OutsideReplayWindow)]
    public void ReplayWindowIsFiveMinutesEitherWay(int offsetSeconds, KodoWebhookVerification expected)
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);

        Assert.Equal(expected, Verify(key, signature, _body, _now.AddSeconds(offsetSeconds)));
    }

    [Fact]
    public void ReplayWindowIsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), KodoWebhookSigner.ReplayWindow);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAMalformedTimestamp(string? timestamp)
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);

        KodoWebhookVerification result = KodoWebhookSigner.Verify(
            [key],
            signature.KeyId,
            timestamp,
            signature.Signature,
            _body,
            _now);

        Assert.Equal(KodoWebhookVerification.Malformed, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=abcd")]
    [InlineData("v1=abc")]
    [InlineData("v1=zz")]
    [InlineData("v1=00")]
    public void RefusesAMalformedSignature(string? signatureHeader)
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");

        KodoWebhookVerification result = KodoWebhookSigner.Verify(
            [key],
            "k1",
            _now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            signatureHeader,
            _body,
            _now);

        Assert.Equal(KodoWebhookVerification.Malformed, result);
    }

    [Fact]
    public void RefusesAMissingKeyId()
    {
        KodoWebhookSigningKey key = KodoExportTestData.NewKey("k1");
        KodoWebhookSignature signature = KodoWebhookSigner.Sign(key, _now, _body);

        KodoWebhookVerification result = KodoWebhookSigner.Verify([key], null, signature.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), signature.Signature, _body, _now);

        Assert.Equal(KodoWebhookVerification.Malformed, result);
    }

    [Fact]
    public void KeyToStringNeverShowsTheSecret()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(KodoWebhookSigningKey.MinimumSecretLength);
        KodoWebhookSigningKey key = new("k1", secret);

        string text = key.ToString();

        Assert.Contains("k1", text);
        Assert.DoesNotContain(Convert.ToBase64String(secret), text);
        Assert.DoesNotContain(Convert.ToHexStringLower(secret), text);
    }

    [Fact]
    public void KeyRefusesAShortSecret()
    {
        Assert.Throws<ArgumentException>(() => new KodoWebhookSigningKey("k1", new byte[KodoWebhookSigningKey.MinimumSecretLength - 1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void KeyRefusesAKeyIdThatIsNotUrlSafe(string keyId)
    {
        Assert.Throws<ArgumentException>(() => new KodoWebhookSigningKey(keyId, new byte[KodoWebhookSigningKey.MinimumSecretLength]));
    }

    [Fact]
    public void KeyRefusesAKeyIdLongerThan64()
    {
        string keyId = new('a', 65);

        Assert.Throws<ArgumentException>(() => new KodoWebhookSigningKey(keyId, new byte[KodoWebhookSigningKey.MinimumSecretLength]));
    }

    private static KodoWebhookVerification Verify(
        KodoWebhookSigningKey key,
        KodoWebhookSignature signature,
        byte[] body,
        DateTimeOffset now) =>
        Verify([key], signature, body, now);

    private static KodoWebhookVerification Verify(
        IEnumerable<KodoWebhookSigningKey> keys,
        KodoWebhookSignature signature,
        byte[] body,
        DateTimeOffset now) =>
        KodoWebhookSigner.Verify(
            keys,
            signature.KeyId,
            signature.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            signature.Signature,
            body,
            now);
}
