using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume.Tests;

/// <summary><c>storage init</c> (doc 63 sections 2, 3, 6, 8 and 10): the only place a volume is created, and every case where it refuses or leaves things alone.</summary>
public sealed class StorageCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-sc-tests-" + Guid.CreateVersion7().ToString("N"));
    private readonly string _installId = Guid.CreateVersion7().ToString();
    private readonly string _volumeId = Guid.CreateVersion7().ToString();
    private readonly string _bootRoot;
    private readonly string _dataRoot;
    private readonly string _container;
    private readonly CreatingOperator _operator;
    private readonly FakeDataVolumeProvider _provider;
    private readonly StringWriter _output = new();

    public StorageCommandTests()
    {
        _bootRoot = Path.Combine(_root, "boot");
        _dataRoot = Path.Combine(_root, "data");
        _container = Path.Combine(_root, "container.img");
        Directory.CreateDirectory(_root);
        _operator = new CreatingOperator(FakeDataVolumeProvider.ProviderKind, _volumeId, _dataRoot);
        _provider = new FakeDataVolumeProvider(_volumeId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string KekPath => Path.Combine(_bootRoot, "keys", "kek.bin");

    private string DescriptorPath => Path.Combine(_bootRoot, "volume", "descriptor.json");

    private IConfiguration Config(string? bootRoot = null, string? dataRoot = null, string instanceClass = "Production", string? installId = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.BootRootKey] = bootRoot ?? _bootRoot,
            [DataVolumeOptions.DataRootKey] = dataRoot ?? _dataRoot,
            [DataVolumeOptions.InstallIdKey] = installId ?? _installId,
            [DataVolumeOptions.InstanceClassKey] = instanceClass,
            [KekComponent.KekProviderKey] = "file",
        }).Build();

    private Task<int> RunAsync(string[] args, IConfiguration? config = null, bool withOperator = true) =>
        StorageCommand.RunAsync(
            args,
            config ?? Config(),
            withOperator ? [_operator] : [],
            [_provider, new DevPlainDataVolumeProvider()],
            () => KekProviderFactory.Create("file", _bootRoot, KekOpenMode.CreateIfMissing),
            _output,
            Ct);

    private string[] InitArgs(params string[] extra) =>
        ["init", "--provider", FakeDataVolumeProvider.ProviderKind, "--container", _container, "--max-gb", "4", "--service-account", "svc", .. extra];

    private void AssertNoKeyInOutput()
    {
        var text = _output.ToString();
        var key = _operator.KeyCopy;
        Assert.NotEmpty(key);
        Assert.DoesNotContain(Convert.ToHexString(key), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(key), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_writes_a_descriptor_whose_wrapped_key_is_the_one_the_operator_received()
    {
        var code = await RunAsync(InitArgs());

        Assert.Equal(0, code);
        Assert.Equal(1, _operator.CreateCalls);
        Assert.Equal(_dataRoot, _operator.Request!.MountPath);
        Assert.Equal(_container, _operator.Request.ContainerPath);
        Assert.Equal("file", _operator.Request.ContainerKind);
        Assert.Equal(4L * 1024 * 1024 * 1024, _operator.Request.MaxSizeBytes);
        Assert.Equal("svc", _operator.Request.ServiceAccount);

        var descriptor = await DescriptorStore.ReadAsync(DescriptorPath, Ct);
        Assert.NotNull(descriptor);
        Assert.Equal(_volumeId, descriptor.VolumeId);
        Assert.Equal(_installId, descriptor.InstallId);
        Assert.Equal(FakeDataVolumeProvider.ProviderKind, descriptor.ProviderKind);
        Assert.Equal(DataVolumeCiphers.XtsAes256, descriptor.Cipher);

        using var kek = FileKekProvider.Open(KekPath, KekOpenMode.OpenExisting);
        var unwrapped = await VolumeKeyCustody.UnwrapVolumeKeyAsync(kek, descriptor, Ct);
        Assert.Equal(VolumeKeyCustody.KeySize, unwrapped.Length);
        Assert.Equal(_operator.KeyCopy, unwrapped);

        var text = _output.ToString();
        Assert.Contains($"volume {_volumeId}", text);
        Assert.Contains(FakeDataVolumeProvider.ProviderKind, text);
        Assert.Contains("initdb", text);
        AssertNoKeyInOutput();
    }

    [Fact]
    public async Task The_buffer_handed_to_the_operator_is_zero_after_it_returns()
    {
        Assert.Equal(0, await RunAsync(InitArgs()));

        Assert.NotEmpty(_operator.KeyCopy);
        Assert.Equal(VolumeKeyCustody.KeySize, _operator.KeyReference.Length);
        Assert.All(_operator.KeyReference.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Dry_run_creates_nothing_and_reports_the_plan()
    {
        var code = await RunAsync(InitArgs("--dry-run"));

        Assert.Equal(0, code);
        Assert.Equal(0, _operator.CreateCalls);
        Assert.False(Directory.Exists(_bootRoot));
        Assert.False(Directory.Exists(_dataRoot));
        Assert.False(File.Exists(_container));

        var text = _output.ToString();
        Assert.Contains(_container, text);
        Assert.Contains(DataVolumeCiphers.XtsAes256, text);
        Assert.Contains(FakeDataVolumeProvider.ProviderKind, text);
        Assert.Contains("will be created", text);
    }

    [Fact]
    public async Task Second_init_refuses_and_leaves_the_first_volume_alone()
    {
        Assert.Equal(0, await RunAsync(InitArgs()));
        var before = await File.ReadAllBytesAsync(DescriptorPath, Ct);

        // A fresh operator and an emptied container path: only the descriptor stands in the way.
        File.Delete(_container);
        Directory.Delete(_dataRoot, recursive: true);
        _output.GetStringBuilder().Clear();
        var code = await RunAsync(InitArgs());

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Equal(1, _operator.CreateCalls);
        Assert.Contains("already exists", _output.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(DescriptorPath, Ct));
    }

    [Fact]
    public async Task A_non_empty_data_root_is_refused()
    {
        Directory.CreateDirectory(_dataRoot);
        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "something"), "x", Ct);

        var code = await RunAsync(InitArgs());

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Equal(0, _operator.CreateCalls);
        Assert.Contains("not empty", _output.ToString());
        Assert.False(File.Exists(KekPath));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("data")]
    public async Task Boot_root_and_data_root_must_not_contain_one_another(string outer)
    {
        var parent = Path.Combine(_root, "parent");
        var config = outer == "boot"
            ? Config(bootRoot: parent, dataRoot: Path.Combine(parent, "data"))
            : Config(bootRoot: Path.Combine(parent, "boot"), dataRoot: parent);

        var code = await RunAsync(InitArgs(), config);

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Equal(0, _operator.CreateCalls);
        Assert.Contains("data_volume_not_initialised", _output.ToString());
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("data")]
    [InlineData("both")]
    public async Task An_unset_root_is_refused(string unset)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.BootRootKey] = unset is "boot" or "both" ? null : _bootRoot,
            [DataVolumeOptions.DataRootKey] = unset is "data" or "both" ? null : _dataRoot,
        }).Build();

        var code = await RunAsync(InitArgs(), config);

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Contains("data_volume_not_initialised", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task A_missing_operator_is_refused_before_any_key_is_made()
    {
        var code = await RunAsync(InitArgs(), withOperator: false);

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Contains("not available", _output.ToString());
        Assert.False(File.Exists(KekPath));
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public async Task An_existing_container_is_refused_and_untouched()
    {
        await File.WriteAllTextAsync(_container, "precious", Ct);

        var code = await RunAsync(InitArgs());

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Equal(0, _operator.CreateCalls);
        Assert.Equal("precious", await File.ReadAllTextAsync(_container, Ct));
    }

    [Fact]
    public async Task A_container_inside_the_data_root_is_refused()
    {
        var code = await RunAsync(["init", "--provider", FakeDataVolumeProvider.ProviderKind, "--container", Path.Combine(_dataRoot, "c.img"), "--max-gb", "4", "--service-account", "svc"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task Dev_plain_is_refused_for_a_production_instance()
    {
        var code = await RunAsync(["init", "--provider", "dev-plain"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, code);
        Assert.Contains("data_volume_not_encrypted", _output.ToString());
        Assert.False(File.Exists(DescriptorPath));
        Assert.False(File.Exists(KekPath));
    }

    [Fact]
    public async Task Dev_plain_writes_a_descriptor_and_marker_with_no_operator_call()
    {
        var code = await RunAsync(["init", "--provider", "dev-plain"], Config(instanceClass: "Development"));

        Assert.Equal(0, code);
        Assert.Equal(0, _operator.CreateCalls);
        var descriptor = await DescriptorStore.ReadAsync(DescriptorPath, Ct);
        Assert.NotNull(descriptor);
        Assert.Equal(DevPlainDataVolumeProvider.ProviderKind, descriptor.ProviderKind);
        Assert.Equal(InstanceClass.Development, descriptor.InstanceClass);
        Assert.True(File.Exists(Path.Combine(_dataRoot, VolumeMarker.FileName)));
        Assert.Contains("dev-plain", _output.ToString());
    }

    [Fact]
    public async Task Failure_after_create_leaves_the_container_and_reports_what_exists()
    {
        // The volume reports itself as unencrypted, so the post-create check fails after the operator succeeded.
        _provider.Status = new DataVolumeStatus(Mounted: true, _volumeId, Encrypted: false, ProtectionOn: false, Cipher: null);

        var code = await RunAsync(InitArgs());

        Assert.Equal(StorageCommand.FailedExitCode, code);
        Assert.Equal(1, _operator.CreateCalls);
        Assert.True(File.Exists(_container));
        Assert.True(File.Exists(DescriptorPath));
        var text = _output.ToString();
        Assert.Contains("storage init failed", text);
        Assert.Contains("Nothing was deleted", text);
        Assert.Contains(_container, text);
        Assert.Contains("descriptor: written", text);
        Assert.All(_operator.KeyReference.ToArray(), b => Assert.Equal(0, b));
        AssertNoKeyInOutput();
    }

    [Fact]
    public async Task An_operator_failure_keeps_any_partial_container_and_the_new_kek()
    {
        _operator.Failure = new InvalidOperationException("cryptsetup failed");

        var code = await RunAsync(InitArgs());

        Assert.Equal(StorageCommand.FailedExitCode, code);
        Assert.True(File.Exists(_container));
        Assert.False(File.Exists(DescriptorPath));
        Assert.True(File.Exists(KekPath));
        var text = _output.ToString();
        Assert.Contains("cryptsetup failed", text);
        Assert.Contains("descriptor: not written", text);
        Assert.Contains("KEK created by this run is left in place", text);
        Assert.All(_operator.KeyReference.ToArray(), b => Assert.Equal(0, b));
        AssertNoKeyInOutput();
    }

    [Theory]
    [InlineData("")]
    [InlineData("create")]
    [InlineData("init --bogus")]
    [InlineData("init")]
    [InlineData("init --provider fake-encrypted --container-kind tape")]
    [InlineData("init --provider fake-encrypted --max-gb zero")]
    [InlineData("init --provider fake-encrypted --dry-run --dry-run")]
    public async Task Bad_arguments_print_usage_and_exit_64(string line)
    {
        var args = line.Length == 0 ? [] : line.Split(' ');

        var code = await RunAsync(args);

        Assert.Equal(VolumeCommand.UsageExitCode, code);
        Assert.Contains("usage: storage init", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
        Assert.False(Directory.Exists(_bootRoot));
    }

    [Fact]
    public async Task A_file_container_without_a_size_or_a_service_account_is_a_usage_error()
    {
        Assert.Equal(VolumeCommand.UsageExitCode, await RunAsync(["init", "--provider", FakeDataVolumeProvider.ProviderKind, "--service-account", "svc"]));
        Assert.Equal(VolumeCommand.UsageExitCode, await RunAsync(["init", "--provider", FakeDataVolumeProvider.ProviderKind, "--max-gb", "4"]));
        Assert.Equal(0, _operator.CreateCalls);
    }

    /// <summary>Creates the container file and the mount folder like a real operator, copies the key it is given, and keeps the live buffer so a test can see it zeroed.</summary>
    private sealed class CreatingOperator(string kind, string volumeId, string dataRoot) : IDataVolumeOperator
    {
        public string Kind => kind;

        public int CreateCalls { get; private set; }

        public DataVolumeCreateRequest? Request { get; private set; }

        public byte[] KeyCopy { get; private set; } = [];

        public ReadOnlyMemory<byte> KeyReference { get; private set; }

        public Exception? Failure { get; set; }

        public ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
        {
            CreateCalls++;
            Request = request;
            KeyCopy = volumeKey.ToArray();
            KeyReference = volumeKey;
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(request.ContainerPath, "container");
            return Failure is null ? ValueTask.FromResult(volumeId) : ValueTask.FromException<string>(Failure);
        }

        public ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
