using System.Text;

namespace Bazaar.Server.Interrupts.Tests;

public class InterruptReasonCipherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _author = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_sealed_reason_opens_to_the_original_text_under_the_authors_key()
    {
        var cipher = new GcBodyInterruptReasonCipher(new FakeBodyCodec());

        var sealed_ = await cipher.SealAsync(_author, "Résumé: build red", Ct);

        Assert.Equal("Résumé: build red", await cipher.OpenAsync(sealed_, Ct));
        Assert.Equal(FakeBodyCodec.KeyFor(_author), sealed_.KeyId);
        Assert.Equal(12, sealed_.Nonce.Length);
        Assert.DoesNotContain("build", Encoding.UTF8.GetString(sealed_.Ciphertext));
    }

    [Fact]
    public async Task A_reason_whose_key_was_erased_opens_as_null()
    {
        var codec = new FakeBodyCodec();
        var cipher = new GcBodyInterruptReasonCipher(codec);
        var sealed_ = await cipher.SealAsync(_author, "gone soon", Ct);

        codec.Erase(_author);

        Assert.Null(await cipher.OpenAsync(sealed_, Ct));
    }

    [Fact]
    public async Task A_blank_reason_is_refused()
    {
        var cipher = new GcBodyInterruptReasonCipher(new FakeBodyCodec());

        await Assert.ThrowsAsync<ArgumentException>(async () => await cipher.SealAsync(_author, " ", Ct));
    }
}
