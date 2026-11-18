using System.Runtime.Versioning;
using System.Security.Cryptography;
using Bazaar.Secrets.Windows;

namespace Bazaar.Secrets.Windows.Tests;

/// <summary>
/// Runs against the real CNG store on this machine. Every test uses its own key name and deletes it on the way out,
/// so nothing is left behind in the machine key store.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKekProviderTests : IDisposable
{
    private readonly List<string> _keyNames = [];

    public void Dispose()
    {
        foreach (var name in _keyNames)
        {
            foreach (var provider in new[] { "Microsoft Platform Crypto Provider", "Microsoft Software Key Storage Provider" })
            {
                try
                {
                    var cng = new CngProvider(provider);
                    if (CngKey.Exists(name, cng, CngKeyOpenOptions.None))
                    {
                        using var key = CngKey.Open(name, cng, CngKeyOpenOptions.None);
                        key.Delete();
                    }
                }
                catch (CryptographicException)
                {
                    // The key lives in the other provider, or the store refused. Cleanup is best-effort.
                }
            }
        }
    }

    private string NewKeyName()
    {
        var name = $"bazaar-test-kek-{Guid.NewGuid():N}";
        _keyNames.Add(name);
        return name;
    }

    [Fact]
    public async Task Wrap_then_unwrap_round_trips_the_key()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        var key = RandomNumberGenerator.GetBytes(32);
        var ad = "scope:alpha|name:token|v1"u8.ToArray();

        var wrapped = await kek.WrapAsync(key, ad, CancellationToken.None);
        var opened = await kek.UnwrapAsync(kek.CurrentKeyId, wrapped, ad, CancellationToken.None);

        Assert.Equal(key, opened.ToArray());
    }

    [Fact]
    public async Task Wrapping_is_randomised_so_equal_keys_give_different_blobs()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        var key = RandomNumberGenerator.GetBytes(32);
        var ad = "ad"u8.ToArray();

        var first = await kek.WrapAsync(key, ad, CancellationToken.None);
        var second = await kek.WrapAsync(key, ad, CancellationToken.None);

        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task Wrong_associated_data_fails_authentication()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = await kek.WrapAsync(key, "scope:alpha"u8.ToArray(), CancellationToken.None);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            kek.UnwrapAsync(kek.CurrentKeyId, wrapped, "scope:beta"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_ciphertext_fails_authentication()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        var key = RandomNumberGenerator.GetBytes(32);
        var ad = "ad"u8.ToArray();
        var wrapped = (await kek.WrapAsync(key, ad, CancellationToken.None)).ToArray();
        wrapped[^1] ^= 0x01;

        await Assert.ThrowsAsync<CryptographicException>(() =>
            kek.UnwrapAsync(kek.CurrentKeyId, wrapped, ad, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_format_is_refused()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            kek.UnwrapAsync(kek.CurrentKeyId, "not a wrapped key at all, long enough to pass the length check"u8.ToArray(), Array.Empty<byte>(), CancellationToken.None));
    }

    [Fact]
    public async Task Old_wraps_still_open_after_rotation_to_a_new_key()
    {
        var oldName = NewKeyName();
        var newName = NewKeyName();
        var key = RandomNumberGenerator.GetBytes(32);
        var ad = "ad"u8.ToArray();

        byte[] wrapped;
        using (var before = new WindowsKekProvider(oldName, WindowsKekCustody.Software))
        {
            wrapped = (await before.WrapAsync(key, ad, CancellationToken.None)).ToArray();
        }

        // The rotated provider has a new current key, and it can still open wraps sealed under the old one.
        using var after = new WindowsKekProvider(newName, WindowsKekCustody.Software);
        Assert.Equal(newName, after.CurrentKeyId);

        var opened = await after.UnwrapAsync(oldName, wrapped, ad, CancellationToken.None);
        Assert.Equal(key, opened.ToArray());
    }

    [Fact]
    public async Task Unwrap_under_a_key_name_that_does_not_exist_fails_clearly()
    {
        using var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        var wrapped = await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), "ad"u8.ToArray(), CancellationToken.None);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            kek.UnwrapAsync("bazaar-test-kek-missing", wrapped, "ad"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public void Automatic_custody_reports_the_level_it_actually_got()
    {
        using var kek = new WindowsKekProvider(NewKeyName());

        // Either level is acceptable here: TPM where this machine has one, software otherwise. The test pins that a level is reported.
        Assert.True(kek.Custody is WindowsKekCustody.Tpm or WindowsKekCustody.Software);
    }

    [Fact]
    public async Task Disposed_provider_refuses_further_work()
    {
        var kek = new WindowsKekProvider(NewKeyName(), WindowsKekCustody.Software);
        kek.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            kek.WrapAsync(new byte[32], Array.Empty<byte>(), CancellationToken.None));
    }
}
