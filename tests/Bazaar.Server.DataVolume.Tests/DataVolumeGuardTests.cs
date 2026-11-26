using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume.Tests;

/// <summary>The start guard: every refusal of doc 63 section 5, and the rule that it never creates anything.</summary>
public sealed class DataVolumeGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-dv-tests-" + Guid.CreateVersion7().ToString("N"));
    private readonly string _installId = Guid.CreateVersion7().ToString();
    private readonly string _volumeId = Guid.CreateVersion7().ToString();
    private readonly FileKekProvider _kek;
    private readonly FakeDataVolumeProvider _provider;

    public DataVolumeGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "boot"));
        _kek = FileKekProvider.Open(Path.Combine(_root, "boot", "keys", "kek.bin"));
        _provider = new FakeDataVolumeProvider(_volumeId);
    }

    public void Dispose()
    {
        _kek.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DataVolumeOptions Options(InstanceClass instanceClass = InstanceClass.Production, bool developmentEnvironment = false) => new()
    {
        BootRoot = Path.Combine(_root, "boot"),
        DataRoot = Path.Combine(_root, "data"),
        InstallId = _installId,
        VolumeId = _volumeId,
        InstanceClass = instanceClass,
        IsDevelopmentEnvironment = developmentEnvironment,
    };

    private NewDataVolume NewVolume(string kind = FakeDataVolumeProvider.ProviderKind, InstanceClass instanceClass = InstanceClass.Production) =>
        new(_installId, _volumeId, kind, "file", Path.Combine(_root, "boot", "volume", "container"), kind == DevPlainDataVolumeProvider.ProviderKind ? DataVolumeCiphers.None : DataVolumeCiphers.XtsAes256, instanceClass);

    private async Task<DataVolumeDescriptor> InitializeAsync(DataVolumeOptions options, NewDataVolume? volume = null)
    {
        Directory.CreateDirectory(options.DataRoot);
        volume ??= NewVolume();
        var key = volume.ProviderKind == DevPlainDataVolumeProvider.ProviderKind ? [] : VolumeKeyCustody.NewKey();
        return await DataVolumeInitializer.InitializeAsync(options, volume, key, _kek, TimeProvider.System, Ct);
    }

    private DataVolumeGuard Guard(DataVolumeOptions options, IKekProvider? kek = null) =>
        new(options, kek ?? _kek, [_provider, new DevPlainDataVolumeProvider()]);

    private static async Task<DataVolumeRefusedException> RefusedAsync(DataVolumeGuard guard) =>
        await Assert.ThrowsAsync<DataVolumeRefusedException>(async () => await guard.VerifyAsync(Ct));

    [Fact]
    public async Task Verifies_an_initialised_encrypted_volume()
    {
        var options = Options();
        var descriptor = await InitializeAsync(options);

        var result = await Guard(options).VerifyAsync(Ct);

        Assert.Equal(descriptor.VolumeId, result.Descriptor.VolumeId);
        Assert.True(result.Status.Encrypted);
        Assert.Null(result.PostgresSystemIdentifier);
    }

    [Fact]
    public async Task A_missing_descriptor_is_refused_and_nothing_is_created()
    {
        // The chaos finding (PR #872): an unmounted root must never look like a fresh install.
        var options = Options();

        var refused = await RefusedAsync(Guard(options));

        Assert.Equal(DataVolumeRefusal.NotInitialised, refused.Reason);
        Assert.Equal("data_volume_not_initialised", refused.Code);
        Assert.False(Directory.Exists(options.DataRoot));
        Assert.False(File.Exists(options.DescriptorPath));
        Assert.False(Directory.Exists(Path.Combine(options.BootRoot, "volume")));
    }

    [Fact]
    public async Task A_missing_data_root_is_refused_and_not_created()
    {
        var options = Options();
        await InitializeAsync(options);
        Directory.Delete(options.DataRoot, recursive: true);

        var refused = await RefusedAsync(Guard(options));

        Assert.Equal(DataVolumeRefusal.NotMounted, refused.Reason);
        Assert.False(Directory.Exists(options.DataRoot));
    }

    [Fact]
    public async Task An_empty_folder_where_the_volume_should_be_mounted_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        File.Delete(options.MarkerPath);

        Assert.Equal(DataVolumeRefusal.MarkerInvalid, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task An_unmounted_data_root_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        _provider.Status = _provider.Status with { Mounted = false };

        Assert.Equal(DataVolumeRefusal.NotMounted, (await RefusedAsync(Guard(options))).Reason);
    }

    [Theory]
    [InlineData(false, true, DataVolumeCiphers.XtsAes256)]
    [InlineData(true, false, DataVolumeCiphers.XtsAes256)]
    [InlineData(true, true, "aes-cbc-128")]
    [InlineData(true, true, null)]
    public async Task An_unencrypted_suspended_or_weak_volume_is_refused(bool encrypted, bool protectionOn, string? cipher)
    {
        var options = Options();
        await InitializeAsync(options);
        _provider.Status = _provider.Status with { Encrypted = encrypted, ProtectionOn = protectionOn, Cipher = cipher };

        var refused = await RefusedAsync(Guard(options));

        Assert.Equal(DataVolumeRefusal.NotEncrypted, refused.Reason);
        Assert.Equal(78, DataVolumeRefusedException.ExitCode);
    }

    [Fact]
    public async Task Another_encrypted_volume_at_the_data_root_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        _provider.Status = _provider.Status with { VolumeId = Guid.CreateVersion7().ToString() };

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task Host_config_naming_another_volume_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        options.VolumeId = Guid.CreateVersion7().ToString();

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_descriptor_with_an_edited_volume_id_does_not_unwrap()
    {
        var options = Options();
        await InitializeAsync(options);
        var otherVolume = Guid.CreateVersion7().ToString();
        var json = await File.ReadAllTextAsync(options.DescriptorPath, Ct);
        await File.WriteAllTextAsync(options.DescriptorPath, json.Replace(_volumeId, otherVolume, StringComparison.Ordinal), Ct);
        options.VolumeId = otherVolume;
        _provider.Status = _provider.Status with { VolumeId = otherVolume };

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_damaged_descriptor_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        await File.WriteAllTextAsync(options.DescriptorPath, "{", Ct);

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_different_key_of_the_same_kek_kind_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        using var otherKek = FileKekProvider.Open(Path.Combine(_root, "other", "kek.bin"));

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options, otherKek))).Reason);
    }

    [Fact]
    public async Task An_edited_marker_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        var json = await File.ReadAllTextAsync(options.MarkerPath, Ct);
        await File.WriteAllTextAsync(options.MarkerPath, json.Replace("Production", "Development", StringComparison.Ordinal), Ct);

        Assert.Equal(DataVolumeRefusal.MarkerInvalid, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_marker_from_another_install_is_refused()
    {
        var options = Options();
        await InitializeAsync(options);
        var foreignKey = VolumeKeyCustody.NewKey();
        await VolumeMarker.WriteAsync(
            options.MarkerPath,
            new VolumeMarkerContent(_installId, _volumeId, InstanceClass.Production, DateTimeOffset.UtcNow, null),
            foreignKey,
            Ct);

        Assert.Equal(DataVolumeRefusal.MarkerInvalid, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task Dev_plain_is_accepted_only_for_a_development_instance_in_the_development_environment()
    {
        var options = Options(InstanceClass.Development, developmentEnvironment: true);
        await InitializeAsync(options, NewVolume(DevPlainDataVolumeProvider.ProviderKind, InstanceClass.Development));

        var result = await Guard(options).VerifyAsync(Ct);
        Assert.False(result.Status.Encrypted);

        options.IsDevelopmentEnvironment = false;
        Assert.Equal(DataVolumeRefusal.NotEncrypted, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_development_instance_cannot_be_switched_to_production_in_config()
    {
        var options = Options(InstanceClass.Development, developmentEnvironment: true);
        await InitializeAsync(options, NewVolume(DevPlainDataVolumeProvider.ProviderKind, InstanceClass.Development));
        options.InstanceClass = InstanceClass.Production;

        Assert.Equal(DataVolumeRefusal.IdentityMismatch, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task Dev_plain_cannot_be_initialised_for_a_production_instance()
    {
        var options = Options();
        Directory.CreateDirectory(options.DataRoot);

        await Assert.ThrowsAsync<ArgumentException>(async () => await DataVolumeInitializer.InitializeAsync(
            options, NewVolume(DevPlainDataVolumeProvider.ProviderKind), ReadOnlyMemory<byte>.Empty, _kek, TimeProvider.System, Ct));
    }

    [Fact]
    public async Task Initialising_twice_is_refused_and_keeps_the_first_descriptor()
    {
        var options = Options();
        await InitializeAsync(options);
        var before = await File.ReadAllBytesAsync(options.DescriptorPath, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeAsync(options));

        Assert.Equal(before, await File.ReadAllBytesAsync(options.DescriptorPath, Ct));
    }

    [Fact]
    public async Task The_descriptor_holds_no_plaintext_key_and_the_volume_key_unwraps_for_the_volume_unit()
    {
        var options = Options();
        Directory.CreateDirectory(options.DataRoot);
        var key = VolumeKeyCustody.NewKey();

        var descriptor = await DataVolumeInitializer.InitializeAsync(options, NewVolume(), key, _kek, TimeProvider.System, Ct);

        var stored = await File.ReadAllTextAsync(options.DescriptorPath, Ct);
        Assert.DoesNotContain(Convert.ToBase64String(key), stored, StringComparison.Ordinal);
        Assert.Equal(-1, (await File.ReadAllBytesAsync(options.DescriptorPath, Ct)).AsSpan().IndexOf(key));
        Assert.Equal(key, await VolumeKeyCustody.UnwrapVolumeKeyAsync(_kek, descriptor, Ct));
    }

    [Fact]
    public async Task The_cluster_system_identifier_is_recorded_and_returned()
    {
        var options = Options();
        var descriptor = await InitializeAsync(options);

        await DataVolumeInitializer.RecordPostgresSystemIdentifierAsync(options, descriptor, "7436920158223411203", _kek, Ct);

        Assert.Equal("7436920158223411203", (await Guard(options).VerifyAsync(Ct)).PostgresSystemIdentifier);
    }

    [Fact]
    public async Task An_external_database_is_refused_unless_attested()
    {
        var options = Options();
        options.DatabaseIsExternal = true;
        await InitializeAsync(options);

        var refused = await RefusedAsync(Guard(options));

        Assert.Equal(DataVolumeRefusal.ExternalDatabaseNotAttested, refused.Reason);
        Assert.Equal("external_database_not_attested", refused.Code);
    }

    [Fact]
    public async Task An_attestation_without_a_method_does_not_count()
    {
        var options = Options();
        options.DatabaseIsExternal = true;
        options.ExternalAtRestAttested = true;
        options.ExternalAtRestMethod = "  ";
        await InitializeAsync(options);

        Assert.Equal(DataVolumeRefusal.ExternalDatabaseNotAttested, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task An_attested_external_database_passes_and_is_reported_as_not_verified()
    {
        var options = Options();
        options.DatabaseIsExternal = true;
        options.ExternalAtRestAttested = true;
        options.ExternalAtRestMethod = "LUKS2 on the database server";
        await InitializeAsync(options);

        var result = await Guard(options).VerifyAsync(Ct);

        Assert.Equal(DatabaseAtRest.AttestedNotVerified, result.DatabaseAtRest);
        Assert.Equal("attested_not_verified", result.DatabaseAtRestCode);
    }

    [Fact]
    public async Task An_attestation_never_excuses_a_data_root_that_is_not_the_volume()
    {
        var options = Options();
        options.DatabaseIsExternal = true;
        options.ExternalAtRestAttested = true;
        options.ExternalAtRestMethod = "LUKS2 on the database server";
        await InitializeAsync(options);
        File.Delete(options.MarkerPath);

        Assert.Equal(DataVolumeRefusal.MarkerInvalid, (await RefusedAsync(Guard(options))).Reason);
    }

    [Fact]
    public async Task A_managed_database_is_reported_as_verified()
    {
        var options = Options();
        await InitializeAsync(options);

        Assert.Equal("verified", (await Guard(options).VerifyAsync(Ct)).DatabaseAtRestCode);
    }

    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData("External", "refuse", true, false)]
    [InlineData("managed", null, false, false)]
    [InlineData("External", "Attested", true, true)]
    public void Options_read_the_database_mode_and_the_at_rest_choice(string? mode, string? atRest, bool external, bool attested)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.DatabaseModeKey] = mode,
            [DataVolumeOptions.ExternalAtRestKey] = atRest,
            [DataVolumeOptions.ExternalAtRestMethodKey] = " disk encryption ",
        }).Build();

        var options = DataVolumeComponent.ReadOptions(configuration);

        Assert.Equal(external, options.DatabaseIsExternal);
        Assert.Equal(attested, options.ExternalAtRestAttested);
        Assert.Equal("disk encryption", options.ExternalAtRestMethod);
    }

    [Fact]
    public void An_unknown_at_rest_choice_is_an_error_and_never_an_attestation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.ExternalAtRestKey] = "trust-me",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => DataVolumeComponent.ReadOptions(configuration));
    }
}
