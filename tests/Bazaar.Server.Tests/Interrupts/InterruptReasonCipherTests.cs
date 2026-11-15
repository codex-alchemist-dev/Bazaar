using System.Security.Cryptography;
using Bazaar.Server.Interrupts;

namespace Bazaar.Server.Tests.Interrupts;

public class InterruptReasonCipherTests
{
    private static byte[] Key(byte seed) => Enumerable.Range(0, AesGcmInterruptReasonCipher.KeySize).Select(i => (byte)(i + seed)).ToArray();

    private static readonly Guid _keyId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] _aad = [1, 2, 3, 4];

    [Fact]
    public void A_sealed_reason_opens_to_the_original_text()
    {
        var cipher = new AesGcmInterruptReasonCipher(_keyId, Key(1));

        var sealed_ = cipher.Seal("Résumé: build red", _aad);

        Assert.Equal("Résumé: build red", cipher.Open(sealed_, _aad));
        Assert.Equal(_keyId, sealed_.KeyId);
        Assert.Equal(12, sealed_.Nonce.Length);
    }

    [Fact]
    public void Each_seal_uses_a_fresh_nonce()
    {
        var cipher = new AesGcmInterruptReasonCipher(_keyId, Key(1));

        var a = cipher.Seal("same", _aad);
        var b = cipher.Seal("same", _aad);

        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void Different_associated_data_fails_to_open()
    {
        var cipher = new AesGcmInterruptReasonCipher(_keyId, Key(1));
        var sealed_ = cipher.Seal("bound", _aad);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(sealed_, [9, 9, 9, 9]));
    }

    [Fact]
    public void A_different_key_fails_to_open()
    {
        var sealed_ = new AesGcmInterruptReasonCipher(_keyId, Key(1)).Seal("keyed", _aad);
        var other = new AesGcmInterruptReasonCipher(_keyId, Key(2));

        Assert.ThrowsAny<CryptographicException>(() => other.Open(sealed_, _aad));
    }

    [Fact]
    public void A_value_sealed_under_another_key_id_is_refused()
    {
        var sealed_ = new AesGcmInterruptReasonCipher(Guid.NewGuid(), Key(1)).Seal("other", _aad);
        var cipher = new AesGcmInterruptReasonCipher(_keyId, Key(1));

        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(sealed_, _aad));
    }

    [Fact]
    public void A_tampered_ciphertext_fails_to_open()
    {
        var cipher = new AesGcmInterruptReasonCipher(_keyId, Key(1));
        var sealed_ = cipher.Seal("intact", _aad);
        sealed_.Ciphertext[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(sealed_, _aad));
    }

    [Fact]
    public void A_key_that_is_not_32_bytes_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmInterruptReasonCipher(_keyId, new byte[16]));
    }
}
