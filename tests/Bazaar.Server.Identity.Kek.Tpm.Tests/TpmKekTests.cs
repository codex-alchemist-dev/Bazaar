using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek.Tpm;

namespace Bazaar.Server.Identity.Kek.Tpm.Tests;

/// <summary>The TPM provider against a fake sealer: policy, VM custody, fail-closed and the systemd-creds arguments. No TPM needed.</summary>
public sealed class TpmKekTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-tpm-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Seals with a chip key held in the object, so a second "chip" cannot open the blob. Records the policy it was asked for.</summary>
    private sealed class FakeSealer(byte[]? chipKey = null) : ITpmSealer
    {
        private readonly byte[] _chip = chipKey ?? RandomNumberGenerator.GetBytes(32);
        public TpmOptions? LastPolicy;
        public bool FailUnseal;

        public byte[] Seal(ReadOnlySpan<byte> plaintext, TpmOptions options)
        {
            LastPolicy = options;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_chip, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag);
            return [.. nonce, .. cipher, .. tag];
        }

        public byte[] Unseal(ReadOnlySpan<byte> sealedBytes, TpmOptions options)
        {
            if (FailUnseal)
                throw new CryptographicException("policy not satisfied");
            var nonce = sealedBytes[..12];
            var cipher = sealedBytes[12..^16];
            var tag = sealedBytes[^16..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_chip, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
    }

    private sealed class FakeProbe(TpmCustodyClass custody) : IVirtualizationProbe
    {
        public TpmCustodyClass Detect() => custody;
    }

    private TpmOptions Options(bool allowVirtual = false, bool raw = false, string? publicKey = null)
    {
        var key = publicKey ?? (raw ? null : Path.Combine(_root, "policy.pem"));
        if (key is not null && !File.Exists(key))
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(key, "public key");
        }

        return new TpmOptions("7", key, raw, allowVirtual, Path.Combine(_root, "keys", "kek-tpm.cred"));
    }

    [Fact]
    public async Task A_created_kek_reopens_and_opens_its_own_wraps()
    {
        var ct = TestContext.Current.CancellationToken;
        var sealer = new FakeSealer();
        var options = Options();
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = new byte[] { 1, 2, 3 };

        byte[] wrapped;
        using (var first = TpmKekProvider.Open(options, sealer, new FakeProbe(TpmCustodyClass.Hardware)))
            wrapped = await first.WrapAsync(dek, aad, ct);

        using var second = TpmKekProvider.Open(options, sealer, new FakeProbe(TpmCustodyClass.Hardware));
        Assert.Equal(dek, await second.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal("tpm-kek-v1", second.KekId);
        Assert.Equal(TpmCustodyClass.Hardware, second.Custody);
        await Assert.ThrowsAsync<KekUnwrapException>(async () => await second.UnwrapAsync(wrapped, new byte[] { 9 }, ct));
    }

    [Fact]
    public void The_key_file_holds_only_the_sealed_form_and_is_owner_only()
    {
        var options = Options();
        using var provider = TpmKekProvider.Open(options, new FakeSealer(), new FakeProbe(TpmCustodyClass.Hardware));

        var stored = File.ReadAllBytes(options.KeyFile);
        Assert.Equal(12 + 32 + 16, stored.Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(options.KeyFile));
    }

    [Fact]
    public void A_vm_host_is_refused_unless_virtual_custody_was_accepted()
    {
        Assert.Throws<InvalidOperationException>(() => TpmKekProvider.Open(Options(), new FakeSealer(), new FakeProbe(TpmCustodyClass.Virtual)));
        Assert.False(File.Exists(Options().KeyFile));

        using var accepted = TpmKekProvider.Open(Options(allowVirtual: true), new FakeSealer(), new FakeProbe(TpmCustodyClass.Virtual));
        Assert.Equal(TpmCustodyClass.Virtual, accepted.Custody);
    }

    [Fact]
    public void A_tpm_that_will_not_unseal_fails_closed_and_leaves_the_file_alone()
    {
        var options = Options();
        using (TpmKekProvider.Open(options, new FakeSealer(), new FakeProbe(TpmCustodyClass.Hardware)))
        {
        }

        var before = File.ReadAllBytes(options.KeyFile);
        var otherChip = new FakeSealer();
        Assert.Throws<InvalidDataException>(() => TpmKekProvider.Open(options, otherChip, new FakeProbe(TpmCustodyClass.Hardware)));
        Assert.Equal(before, File.ReadAllBytes(options.KeyFile));
    }

    [Fact]
    public void A_failed_policy_is_an_error_never_a_new_key()
    {
        var options = Options();
        var sealer = new FakeSealer();
        using (TpmKekProvider.Open(options, sealer, new FakeProbe(TpmCustodyClass.Hardware)))
        {
        }

        sealer.FailUnseal = true;
        Assert.Throws<InvalidDataException>(() => TpmKekProvider.Open(options, sealer, new FakeProbe(TpmCustodyClass.Hardware)));
    }

    [Fact]
    public void A_sealed_blob_of_the_wrong_size_is_refused()
    {
        var options = Options();
        Directory.CreateDirectory(Path.GetDirectoryName(options.KeyFile)!);
        var sealer = new FakeSealer();
        File.WriteAllBytes(options.KeyFile, sealer.Seal(new byte[16], options));

        Assert.Throws<InvalidDataException>(() => TpmKekProvider.Open(options, sealer, new FakeProbe(TpmCustodyClass.Hardware)));
    }

    [Fact]
    public void The_signed_policy_needs_its_public_key_on_disk()
    {
        var options = Options() with { PublicKeyPath = Path.Combine(_root, "missing.pem") };
        Assert.Throws<FileNotFoundException>(() => TpmKekProvider.Open(options, new FakeSealer(), new FakeProbe(TpmCustodyClass.Hardware)));
    }

    [Fact]
    public void Options_demand_the_signed_policy_or_an_explicit_raw_opt_in()
    {
        Dictionary<string, string?> settings = [];
        Assert.Throws<ArgumentException>(() => TpmOptions.From(k => settings.GetValueOrDefault(k), _root));

        settings[TpmOptions.AllowRawPcrPolicyKey] = "true";
        var raw = TpmOptions.From(k => settings.GetValueOrDefault(k), _root);
        Assert.Null(raw.PublicKeyPath);
        Assert.Equal("7", raw.Pcrs);
        Assert.Equal(Path.Combine(_root, "keys", "kek-tpm.cred"), raw.KeyFile);
        Assert.False(raw.AllowVirtual);

        settings.Clear();
        settings[TpmOptions.PublicKeyPathKey] = Path.Combine(_root, "p.pem");
        settings[TpmOptions.PcrsKey] = "7+11";
        Assert.Equal("7+11", TpmOptions.From(k => settings.GetValueOrDefault(k), _root).Pcrs);
    }

    [Theory]
    [InlineData("24")]
    [InlineData("7,11")]
    [InlineData("seven")]
    [InlineData("7 --with-key=host")]
    public void Options_refuse_a_pcr_list_that_is_not_plain_numbers(string pcrs)
    {
        Dictionary<string, string?> settings = new() { [TpmOptions.AllowRawPcrPolicyKey] = "true", [TpmOptions.PcrsKey] = pcrs };
        Assert.Throws<ArgumentException>(() => TpmOptions.From(k => settings.GetValueOrDefault(k), _root));
    }

    [Fact]
    public void Options_refuse_relative_paths()
    {
        Dictionary<string, string?> settings = new() { [TpmOptions.PublicKeyPathKey] = "policy.pem" };
        Assert.Throws<ArgumentException>(() => TpmOptions.From(k => settings.GetValueOrDefault(k), _root));
    }

    [Fact]
    public void The_systemd_creds_arguments_pin_the_tpm_key_the_pcrs_and_the_signed_policy()
    {
        var signed = new TpmOptions("7", "/etc/bazaar/policy.pem", false, false, "/k");
        Assert.Equal(
            ["encrypt", "--with-key=tpm2", "--tpm2-pcrs=7", "--name=bazaar-kek", "--tpm2-public-key=/etc/bazaar/policy.pem", "-", "-"],
            SystemdCredsSealer.EncryptArguments(signed));

        var raw = new TpmOptions("7", null, true, false, "/k");
        Assert.DoesNotContain(SystemdCredsSealer.EncryptArguments(raw), a => a.StartsWith("--tpm2-public-key", StringComparison.Ordinal));
        Assert.Equal(["decrypt", "--name=bazaar-kek", "-", "-"], SystemdCredsSealer.DecryptArguments());
    }

    [Fact]
    public void The_kind_is_tpm_and_refuses_a_non_linux_host()
    {
        var kind = new TpmKekKind(new FakeSealer(), new FakeProbe(TpmCustodyClass.Hardware), isLinux: false);
        Assert.Equal("tpm", kind.Kind);
        Assert.Throws<PlatformNotSupportedException>(() => kind.Create(_ => null, _root));
    }

    [Fact]
    public void The_kind_opens_a_provider_from_settings()
    {
        var kind = new TpmKekKind(new FakeSealer(), new FakeProbe(TpmCustodyClass.Hardware), isLinux: true);
        Dictionary<string, string?> settings = new() { [TpmOptions.AllowRawPcrPolicyKey] = "true" };
        using var provider = (TpmKekProvider)kind.Create(k => settings.GetValueOrDefault(k), _root);
        Assert.Equal("tpm-kek-v1", provider.KekId);
    }

    [Fact]
    public void The_component_registers_the_kind_and_the_factory_names_it()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new TpmKekComponent().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        Assert.Equal(["tpm"], Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<IKekProviderKind>(provider).Select(k => k.Kind));
        Assert.Contains("tpm", KekProviderFactory.AllowedKinds);
        Assert.Contains("identity-kek", TpmKekComponent.ManifestDefinition.DependsOn);
        Assert.Throws<NotSupportedException>(() => KekProviderFactory.Create("tpm", _root));
    }
}
