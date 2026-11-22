using Bazaar.Core;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>File and DPAPI-shaped KEK providers, the factory choice and the kernel registration. Database-free.</summary>
public sealed class KekProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-kek-tests-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string KeyPath => Path.Combine(_root, "keys", "kek.bin");

    [Fact]
    public async Task File_provider_wrap_unwraps_after_reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var aad = Guid.CreateVersion7().ToByteArray();
        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        using var first = FileKekProvider.Open(KeyPath);
        var wrapped = await first.WrapAsync(dek, aad, ct);
        using var reopened = FileKekProvider.Open(KeyPath);

        Assert.Equal(dek, await reopened.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal(FileKekProvider.Id, reopened.KekId);
    }

    [Fact]
    public async Task File_provider_rejects_different_associated_data()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = FileKekProvider.Open(KeyPath);
        var wrapped = await kek.WrapAsync(new byte[32], Guid.CreateVersion7().ToByteArray(), ct);

        await Assert.ThrowsAsync<KekUnwrapException>(() => kek.UnwrapAsync(wrapped, Guid.CreateVersion7().ToByteArray(), ct).AsTask());
    }

    [Fact]
    public void File_provider_refuses_a_key_file_of_the_wrong_length()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, new byte[10]);

        Assert.Throws<InvalidDataException>(() => FileKekProvider.Open(KeyPath));
    }

    [Fact]
    public async Task Concurrent_first_starts_agree_on_one_key_and_never_see_a_torn_file()
    {
        // Chaos campaign disk-clock-resources: the key file used to be created empty and then written, so a concurrent start (or a
        // full disk) could leave or read a zero-byte file, which then failed every later start with "does not hold a 32-byte key".
        var ct = TestContext.Current.CancellationToken;
        var aad = Guid.CreateVersion7().ToByteArray();
        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var barrier = new Barrier(16);

        var wraps = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(ct);
            using var kek = FileKekProvider.Open(KeyPath);
            return (kek.KekId, Wrapped: await kek.WrapAsync(dek, aad, ct));
        }, ct)));

        using var reopened = FileKekProvider.Open(KeyPath);
        foreach (var wrap in wraps)
            Assert.Equal(dek, await reopened.UnwrapAsync(wrap.Wrapped, aad, ct));
        Assert.Equal(32, new FileInfo(KeyPath).Length);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(KeyPath)!, "*.tmp"));
    }

    [Fact]
    public void A_failed_create_leaves_no_key_file_behind()
    {
        // The directory exists but the key path is occupied by a directory, so the move fails: nothing may be left as kek.bin.
        var dir = Path.GetDirectoryName(KeyPath)!;
        Directory.CreateDirectory(Path.Combine(dir, "kek.bin"));

        Assert.ThrowsAny<IOException>(() => FileKekProvider.Open(KeyPath));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Key_paths_must_be_absolute()
    {
        Assert.Throws<ArgumentException>(() => FileKekProvider.Open("relative/kek.bin"));
    }

    [Fact]
    public async Task Dpapi_shaped_provider_round_trips_and_stores_no_plaintext_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var protector = TestProtector.Create();
        var aad = Guid.CreateVersion7().ToByteArray();
        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        using var first = DpapiKekProvider.Open(KeyPath, protector);
        var wrapped = await first.WrapAsync(dek, aad, ct);
        using var reopened = DpapiKekProvider.Open(KeyPath, protector);

        Assert.Equal(dek, await reopened.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal(DpapiKekProvider.Id, reopened.KekId);
        Assert.Equal(12 + 32 + 16, new FileInfo(KeyPath).Length); // nonce | ciphertext | tag from the test protector
    }

    [Fact]
    public void Dpapi_shaped_provider_refuses_a_key_file_protected_by_another_host()
    {
        DpapiKekProvider.Open(KeyPath, TestProtector.Create()).Dispose();

        var ex = Assert.Throws<InvalidDataException>(() => DpapiKekProvider.Open(KeyPath, TestProtector.Create()));
        Assert.Contains("re-wrap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dpapi_shaped_provider_refuses_a_plain_file_key()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.Throws<InvalidDataException>(() => DpapiKekProvider.Open(KeyPath, TestProtector.Create()));
    }

    [Fact]
    public async Task Wraps_from_one_provider_do_not_open_under_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var aad = Guid.CreateVersion7().ToByteArray();
        using var file = FileKekProvider.Open(Path.Combine(_root, "file", "kek.bin"));
        using var dpapi = DpapiKekProvider.Open(Path.Combine(_root, "dpapi", "kek.bin"), TestProtector.Create());
        var wrapped = await file.WrapAsync(new byte[32], aad, ct);

        await Assert.ThrowsAsync<KekUnwrapException>(() => dpapi.UnwrapAsync(wrapped, aad, ct).AsTask());
    }

    [Fact]
    public void Factory_defaults_to_the_platform_choice()
    {
        var expected = OperatingSystem.IsWindows() ? "dpapi" : "file";

        Assert.Equal(expected, KekProviderFactory.DefaultKind);
    }

    [Fact]
    public void Factory_opens_file_kind_on_any_host()
    {
        var kek = KekProviderFactory.Create("file", _root);

        Assert.IsType<FileKekProvider>(kek);
        Assert.True(File.Exists(KeyPath));
    }

    [Fact]
    public void Factory_refuses_unknown_and_unbuilt_kinds()
    {
        Assert.Throws<ArgumentException>(() => KekProviderFactory.Create("tpm", _root));
        Assert.Throws<NotSupportedException>(() => KekProviderFactory.Create("openbao", _root));
    }

    [Fact]
    public void Factory_refuses_dpapi_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "DPAPI is available on Windows; this checks the refusal elsewhere.");

        Assert.Throws<PlatformNotSupportedException>(() => KekProviderFactory.Create("dpapi", _root));
    }

    [Fact]
    public void Factory_dpapi_round_trip_on_the_real_host()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real DPAPI runs on a Windows host only (category Windows-host, doc 33).");

        var first = KekProviderFactory.Create("dpapi", _root);
        var second = KekProviderFactory.Create("dpapi", _root);

        Assert.IsType<DpapiKekProvider>(first);
        Assert.Equal(DpapiKekProvider.Id, second.KekId);
    }

    [Fact]
    public void Component_registers_the_provider_through_the_kernel()
    {
        var configuration = new FakeConfiguration(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            [KekComponent.KekProviderKey] = "file",
        });
        var services = new ServiceCollection();

        services.AddBazaarComponents(configuration, [new KekComponent()]);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileKekProvider>(provider.GetRequiredService<IKekProvider>());
        Assert.Equal("identity-kek", KekComponent.ManifestDefinition.Id);
        Assert.Equal(["identity:kek:provider", "identity:dek:service", "secrets:kek"], KekComponent.ManifestDefinition.Provides);
    }

    [Fact]
    public void Component_can_be_disabled_by_configuration()
    {
        var configuration = new FakeConfiguration(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            ["Bazaar:Components:identity-kek:Enabled"] = "false",
        });
        var services = new ServiceCollection();

        services.AddBazaarComponents(configuration, [new KekComponent()]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IKekProvider));
    }
}
