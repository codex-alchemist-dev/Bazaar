using System.Text.Json;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume.Tests;

/// <summary>The volume unit command (doc 63 section 4): open, close, status, and the rules that it never creates a KEK and never leaks a key.</summary>
public sealed class VolumeCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-vc-tests-" + Guid.CreateVersion7().ToString("N"));
    private readonly string _installId = Guid.CreateVersion7().ToString();
    private readonly string _volumeId = Guid.CreateVersion7().ToString();
    private readonly string _bootRoot;
    private readonly string _dataRoot;
    private readonly FakeOperator _operator = new(FakeDataVolumeProvider.ProviderKind);
    private readonly FakeDataVolumeProvider _provider;
    private readonly StringWriter _output = new();
    private byte[] _volumeKey = [];

    public VolumeCommandTests()
    {
        _bootRoot = Path.Combine(_root, "boot");
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_bootRoot);
        Directory.CreateDirectory(_dataRoot);
        _provider = new FakeDataVolumeProvider(_volumeId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string KekPath => Path.Combine(_bootRoot, "keys", "kek.bin");

    private IConfiguration Config(string? installId = null, string? volumeId = null, string instanceClass = "Production") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.BootRootKey] = _bootRoot,
            [DataVolumeOptions.DataRootKey] = _dataRoot,
            [DataVolumeOptions.InstallIdKey] = installId ?? _installId,
            [DataVolumeOptions.VolumeIdKey] = volumeId ?? _volumeId,
            [DataVolumeOptions.InstanceClassKey] = instanceClass,
        }).Build();

    private DataVolumeOptions Options() => DataVolumeComponent.ReadOptions(Config());

    /// <summary>Writes a descriptor the way setup does, wrapped under a real file KEK. The volume key is kept in <see cref="_volumeKey"/>.</summary>
    private async Task InitializeAsync(string kind = FakeDataVolumeProvider.ProviderKind, InstanceClass instanceClass = InstanceClass.Production)
    {
        using var kek = FileKekProvider.Open(KekPath);
        var plain = kind == DevPlainDataVolumeProvider.ProviderKind;
        _volumeKey = plain ? [] : VolumeKeyCustody.NewKey();
        var volume = new NewDataVolume(_installId, _volumeId, kind, "file", Path.Combine(_bootRoot, "volume", "container"), plain ? DataVolumeCiphers.None : DataVolumeCiphers.XtsAes256, instanceClass);
        await DataVolumeInitializer.InitializeAsync(Options(), volume, _volumeKey, kek, TimeProvider.System, Ct);
    }

    private FileKekProvider OpenKek() => FileKekProvider.Open(KekPath, KekOpenMode.OpenExisting);

    private Task<int> RunAsync(string[] args, IConfiguration? config = null, Func<IKekProvider>? openKek = null) =>
        VolumeCommand.RunAsync(args, config ?? Config(), [_operator], [_provider, new DevPlainDataVolumeProvider()], openKek ?? (() => OpenKek()), _output, Ct);

    private void AssertNoKeyInOutput(params byte[][] keys)
    {
        var text = _output.ToString();
        foreach (var key in keys.Where(k => k.Length > 0))
        {
            Assert.DoesNotContain(Convert.ToHexString(key), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(key), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Open_unwraps_the_key_calls_the_operator_and_zeroes_the_buffer()
    {
        await InitializeAsync();

        var exit = await RunAsync(["open"]);

        Assert.Equal(0, exit);
        Assert.Equal(1, _operator.OpenCalls);
        Assert.Equal(_dataRoot, _operator.MountPath);
        Assert.Equal(_volumeKey, _operator.KeyCopyDuringCall);
        // The operator kept the buffer it was handed; after the command returns it must be all zero.
        Assert.Equal(VolumeCommandTestsKeys.Zero, _operator.KeyReference.ToArray());
        AssertNoKeyInOutput(_volumeKey);
    }

    [Fact]
    public async Task Open_zeroes_the_buffer_when_the_operator_fails()
    {
        await InitializeAsync();
        _operator.OpenFailure = new InvalidOperationException("mount failed");

        var exit = await RunAsync(["open"]);

        Assert.Equal(VolumeCommand.FailedExitCode, exit);
        Assert.Equal(VolumeCommandTestsKeys.Zero, _operator.KeyReference.ToArray());
        Assert.Contains("mount failed", _output.ToString(), StringComparison.Ordinal);
        AssertNoKeyInOutput(_volumeKey);
    }

    [Fact]
    public async Task Open_without_a_descriptor_is_refused_and_creates_nothing()
    {
        var exit = await RunAsync(["open"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_not_initialised", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _operator.OpenCalls);
        Assert.False(File.Exists(KekPath));
        Assert.False(Directory.Exists(Path.Combine(_bootRoot, "volume")));
    }

    [Fact]
    public async Task Open_with_a_missing_kek_is_refused_and_never_creates_one()
    {
        await InitializeAsync();
        File.Delete(KekPath);

        var exit = await RunAsync(["open"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_not_initialised", _output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(KekPath));
        Assert.Equal(0, _operator.OpenCalls);
    }

    [Fact]
    public async Task Open_with_another_kek_is_refused()
    {
        await InitializeAsync();
        File.Delete(KekPath);
        FileKekProvider.Open(KekPath).Dispose();

        var exit = await RunAsync(["open"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_identity_mismatch", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _operator.OpenCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Open_and_close_refuse_an_identity_that_differs_from_host_config(bool install)
    {
        await InitializeAsync();
        var config = install ? Config(installId: Guid.CreateVersion7().ToString()) : Config(volumeId: Guid.CreateVersion7().ToString());

        var open = await RunAsync(["open"], config);
        var close = await RunAsync(["close"], config);

        Assert.Equal(DataVolumeRefusedException.ExitCode, open);
        Assert.Equal(DataVolumeRefusedException.ExitCode, close);
        Assert.Contains("data_volume_identity_mismatch", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _operator.OpenCalls);
        Assert.Equal(0, _operator.CloseCalls);
    }

    [Fact]
    public async Task A_damaged_descriptor_is_refused()
    {
        await InitializeAsync();
        await File.WriteAllTextAsync(Options().DescriptorPath, "{ not a descriptor", Ct);

        var exit = await RunAsync(["open"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_identity_mismatch", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_with_no_operator_for_the_descriptor_kind_is_refused()
    {
        await InitializeAsync(kind: "other-kind");

        var exit = await RunAsync(["open"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("other-kind", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _operator.OpenCalls);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("close")]
    public async Task Dev_plain_is_a_no_op_for_a_development_instance_and_needs_no_kek(string verb)
    {
        await InitializeAsync(DevPlainDataVolumeProvider.ProviderKind, InstanceClass.Development);
        File.Delete(KekPath);

        var exit = await RunAsync([verb], Config(instanceClass: "Development"));

        Assert.Equal(0, exit);
        Assert.Equal(0, _operator.OpenCalls + _operator.CloseCalls);
        Assert.False(File.Exists(KekPath));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("close")]
    public async Task Dev_plain_is_refused_for_a_production_descriptor(string verb)
    {
        await InitializeAsync();
        // A production descriptor that names dev-plain cannot come from setup; build one to prove the command refuses it.
        var path = Options().DescriptorPath;
        var text = (await File.ReadAllTextAsync(path, Ct)).Replace(FakeDataVolumeProvider.ProviderKind, DevPlainDataVolumeProvider.ProviderKind, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text, Ct);

        var exit = await RunAsync([verb]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_not_encrypted", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_calls_the_operator_with_the_data_root()
    {
        await InitializeAsync();

        var exit = await RunAsync(["close"]);

        Assert.Equal(0, exit);
        Assert.Equal(1, _operator.CloseCalls);
        Assert.Equal(_dataRoot, _operator.MountPath);
    }

    [Fact]
    public async Task Close_of_a_busy_volume_fails_once_and_is_never_forced()
    {
        await InitializeAsync();
        _operator.CloseFailure = new InvalidOperationException("The data volume is in use: a process still has files open under the mount path.");

        var exit = await RunAsync(["close"]);

        Assert.Equal(VolumeCommand.FailedExitCode, exit);
        Assert.Equal(1, _operator.CloseCalls);
        Assert.Contains("in use", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_does_not_open_the_kek()
    {
        await InitializeAsync();

        var exit = await RunAsync(["close"], openKek: () => throw new InvalidOperationException("must not be called"));

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Status_prints_one_json_line_and_exits_zero_when_protected()
    {
        await InitializeAsync();

        var exit = await RunAsync(["status"]);

        Assert.Equal(0, exit);
        var line = _output.ToString().TrimEnd();
        Assert.DoesNotContain('\n', line);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.True(root.GetProperty("mounted").GetBoolean());
        Assert.Equal(_volumeId, root.GetProperty("volumeId").GetString());
        Assert.True(root.GetProperty("encrypted").GetBoolean());
        Assert.True(root.GetProperty("protectionOn").GetBoolean());
        Assert.Equal(DataVolumeCiphers.XtsAes256, root.GetProperty("cipher").GetString());
        AssertNoKeyInOutput(_volumeKey);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Status_exits_three_unless_mounted_encrypted_and_protected(bool mounted, bool encrypted, bool protectionOn)
    {
        await InitializeAsync();
        _provider.Status = _provider.Status with { Mounted = mounted, Encrypted = encrypted, ProtectionOn = protectionOn };

        var exit = await RunAsync(["status"]);

        Assert.Equal(VolumeCommand.NotProtectedExitCode, exit);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.Equal(mounted, json.RootElement.GetProperty("mounted").GetBoolean());
    }

    [Fact]
    public async Task Status_without_a_descriptor_is_refused()
    {
        var exit = await RunAsync(["status"]);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Contains("data_volume_not_initialised", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("force")]
    [InlineData("OPEN", "--force")]
    [InlineData("close", "now")]
    [InlineData("")]
    public async Task Unknown_verbs_and_extra_arguments_print_usage(params string[] args)
    {
        await InitializeAsync();

        var exit = await RunAsync(args);

        Assert.Equal(VolumeCommand.UsageExitCode, exit);
        Assert.StartsWith("usage:", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _operator.OpenCalls + _operator.CloseCalls);
    }

    [Fact]
    public async Task Verbs_are_case_insensitive()
    {
        await InitializeAsync();

        Assert.Equal(0, await RunAsync(["Status"]));
    }

    [Fact]
    public async Task A_missing_boot_root_setting_is_refused()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DataVolumeOptions.DataRootKey] = _dataRoot }).Build();

        var exit = await RunAsync(["open"], config);

        Assert.Equal(DataVolumeRefusedException.ExitCode, exit);
        Assert.Equal(0, _operator.OpenCalls);
    }

    private static class VolumeCommandTestsKeys
    {
        public static readonly byte[] Zero = new byte[32];
    }

    /// <summary>An operator that records what it was given and keeps the key reference, as a hostile operator might.</summary>
    private sealed class FakeOperator(string kind) : IDataVolumeOperator
    {
        public string Kind => kind;

        public int OpenCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public string? MountPath { get; private set; }

        public ReadOnlyMemory<byte> KeyReference { get; private set; }

        public byte[] KeyCopyDuringCall { get; private set; } = [];

        public Exception? OpenFailure { get; set; }

        public Exception? CloseFailure { get; set; }

        public ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
        {
            OpenCalls++;
            MountPath = mountPath;
            KeyReference = volumeKey;
            KeyCopyDuringCall = volumeKey.ToArray();
            return OpenFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(OpenFailure);
        }

        public ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken)
        {
            CloseCalls++;
            MountPath = mountPath;
            return CloseFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CloseFailure);
        }
    }
}
