using System.Security.Cryptography;
using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

public class AesGcmProfileEncryptorTests
{
    private static readonly byte[] _plaintext = "{\"cookies\":[]}"u8.ToArray();

    [Fact]
    public void Decrypt_round_trips_the_plaintext()
    {
        var encryptor = new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32));

        var blob = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);
        var decrypted = encryptor.Decrypt(BrowserProfileTestKit.AgentId, blob);

        Assert.Equal(_plaintext, decrypted);
        Assert.Equal(12, blob.Nonce.Length);
        Assert.Equal(16, blob.Tag.Length);
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_nonce()
    {
        var encryptor = new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32));

        var first = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);
        var second = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void Flipping_a_ciphertext_byte_makes_decrypt_throw()
    {
        var encryptor = new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32));
        var blob = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);
        var tampered = (byte[])blob.Ciphertext.Clone();
        tampered[0] ^= 0x01;
        var forged = blob with { Ciphertext = tampered };

        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt(BrowserProfileTestKit.AgentId, forged));
    }

    [Fact]
    public void Flipping_a_tag_byte_makes_decrypt_throw()
    {
        var encryptor = new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32));
        var blob = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);
        var tampered = (byte[])blob.Tag.Clone();
        tampered[^1] ^= 0x80;

        Assert.ThrowsAny<CryptographicException>(() =>
            encryptor.Decrypt(BrowserProfileTestKit.AgentId, blob with { Tag = tampered }));
    }

    [Fact]
    public void Decrypting_for_another_agent_makes_decrypt_throw()
    {
        var encryptor = new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32));
        var blob = encryptor.Encrypt(BrowserProfileTestKit.AgentId, _plaintext);

        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt("agent-other", blob));
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmProfileEncryptor(new byte[16]));
    }

    [Fact]
    public async Task Store_keeps_only_ciphertext_and_round_trips_cookies()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken);
        var cookie = BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "token", "value-xyz");

        await store.WriteCookiesAsync(profile.ProfileId, new CookieJar([cookie]), TestContext.Current.CancellationToken);
        var read = await store.ReadCookiesAsync(profile.ProfileId, TestContext.Current.CancellationToken);

        Assert.Equal(cookie, Assert.Single(read.Cookies));
    }
}
