using System.Text.Json;
using Bazaar.Server.DataVolume;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Setup.Storage.Tests;

public sealed class StorageVolumeBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-setup-storage-" + Guid.NewGuid().ToString("N"));

    private string BootRoot => Path.Combine(_root, "boot");

    private string DataRoot => Path.Combine(_root, "data");

    private string DescriptorPath => Path.Combine(BootRoot, "volume", "descriptor.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private IConfiguration Config(bool withBootRoot = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(withBootRoot ? new Dictionary<string, string?> { [DataVolumeOptions.BootRootKey] = BootRoot } : [])
            .Build();

    private SetupPlan Plan(string provider = "dev-plain", string instanceClass = "development", string? dataRoot = null) => new()
    {
        Database = new SetupDatabaseSection { DataRoot = dataRoot ?? DataRoot },
        Storage = new SetupStorageSection { InstanceClass = instanceClass, Volume = new SetupStorageVolumeSection { Provider = provider } },
    };

    /// <summary>The bootstrap over the real storage init with the dev-plain provider and a file KEK under the boot root.</summary>
    private StorageVolumeBootstrap Real(TextWriter? output = null) =>
        StorageVolumeBootstrap.Create(
            Config(),
            [],
            [new DevPlainDataVolumeProvider()],
            () => FileKekProvider.Open(Path.Combine(BootRoot, "keys", "kek.bin"), KekOpenMode.CreateIfMissing),
            output ?? TextWriter.Null);

    private static Dictionary<string, string> ReadHostFile(string bootRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VolumeHostConfigFile.PathFor(bootRoot)));
        return document.RootElement.GetProperty("Bazaar").GetProperty("Host").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    [Fact]
    public async Task A_plan_without_a_provider_asks_for_no_volume_and_touches_nothing()
    {
        var calls = 0;
        var bootstrap = new StorageVolumeBootstrap(Config(), (_, _, _, _) => { calls++; return Task.FromResult(0); }, TextWriter.Null);

        var result = await bootstrap.EnsureAsync(Plan(provider: ""), Ct);

        Assert.Equal(StorageBootstrapOutcome.NotRequested, result.Outcome);
        Assert.True(result.MayStart);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_missing_boot_root_is_refused_with_exit_code_78()
    {
        var bootstrap = new StorageVolumeBootstrap(Config(withBootRoot: false), (_, _, _, _) => Task.FromResult(0), TextWriter.Null);

        var result = await bootstrap.EnsureAsync(Plan(), Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.Equal(78, result.ExitCode);
        Assert.Equal("data_volume_not_initialised", result.Reason);
        Assert.False(result.MayStart);
    }

    [Fact]
    public async Task A_relative_data_root_is_refused()
    {
        var result = await Real().EnsureAsync(Plan(dataRoot: "data"), Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public async Task The_volume_is_created_and_the_host_config_file_names_it()
    {
        var result = await Real().EnsureAsync(Plan(), Ct);

        Assert.Equal(StorageBootstrapOutcome.Created, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        var descriptor = await DescriptorStore.ReadAsync(DescriptorPath, Ct);
        Assert.NotNull(descriptor);
        Assert.Equal("dev-plain", descriptor.ProviderKind);
        Assert.Equal(InstanceClass.Development, descriptor.InstanceClass);

        var host = ReadHostFile(BootRoot);
        Assert.Equal(DataRoot, host["DataRoot"]);
        Assert.Equal(descriptor.InstallId, host["InstallId"]);
        Assert.Equal(descriptor.VolumeId, host["VolumeId"]);
        Assert.Equal("Development", host["InstanceClass"]);
    }

    [Fact]
    public async Task The_written_host_config_makes_the_start_guard_pass()
    {
        await Real().EnsureAsync(Plan(), Ct);

        var config = new ConfigurationBuilder()
            .AddConfiguration(Config())
            .AddJsonFile(VolumeHostConfigFile.PathFor(BootRoot))
            .Build();
        var options = DataVolumeComponent.ReadOptions(config);
        options.IsDevelopmentEnvironment = true;
        using var kek = FileKekProvider.Open(Path.Combine(BootRoot, "keys", "kek.bin"), KekOpenMode.OpenExisting);

        var verified = await new DataVolumeGuard(options, kek, [new DevPlainDataVolumeProvider()]).VerifyAsync(Ct);

        Assert.Equal(options.VolumeId, verified.Descriptor.VolumeId);
    }

    [Fact]
    public async Task A_second_run_changes_nothing_and_never_calls_init_again()
    {
        await Real().EnsureAsync(Plan(), Ct);
        var before = await File.ReadAllBytesAsync(DescriptorPath, Ct);
        File.Delete(VolumeHostConfigFile.PathFor(BootRoot));
        var calls = 0;
        var bootstrap = new StorageVolumeBootstrap(Config(), (_, _, _, _) => { calls++; return Task.FromResult(0); }, TextWriter.Null);

        var result = await bootstrap.EnsureAsync(Plan(), Ct);

        Assert.Equal(StorageBootstrapOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(DescriptorPath, Ct));
        Assert.Equal(DataRoot, ReadHostFile(BootRoot)["DataRoot"]);
    }

    [Theory]
    [InlineData("luks2", "development")]
    [InlineData("dev-plain", "production")]
    public async Task An_existing_volume_that_differs_from_the_plan_is_refused_and_left_alone(string provider, string instanceClass)
    {
        await Real().EnsureAsync(Plan(), Ct);
        var before = await File.ReadAllBytesAsync(DescriptorPath, Ct);
        var calls = 0;
        var bootstrap = new StorageVolumeBootstrap(Config(), (_, _, _, _) => { calls++; return Task.FromResult(0); }, TextWriter.Null);

        var result = await bootstrap.EnsureAsync(Plan(provider, instanceClass), Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.Equal("data_volume_already_initialised", result.Reason);
        Assert.Equal(78, result.ExitCode);
        Assert.Equal(0, calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(DescriptorPath, Ct));
    }

    [Fact]
    public async Task A_data_root_that_already_holds_files_is_refused_by_init()
    {
        Directory.CreateDirectory(DataRoot);
        await File.WriteAllTextAsync(Path.Combine(DataRoot, "PG_VERSION"), "18", Ct);
        var output = new StringWriter();

        var result = await Real(output).EnsureAsync(Plan(), Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.Equal(78, result.ExitCode);
        Assert.Contains("data_volume_already_initialised", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(DescriptorPath));
        Assert.False(File.Exists(VolumeHostConfigFile.PathFor(BootRoot)));
    }

    [Fact]
    public async Task Init_gets_the_plans_data_root_and_instance_class()
    {
        IConfiguration? seen = null;
        string[]? seenArgs = null;
        var bootstrap = new StorageVolumeBootstrap(
            Config(),
            (args, config, _, _) =>
            {
                seen = config;
                seenArgs = args;
                return Task.FromResult(1);
            },
            TextWriter.Null);

        var result = await bootstrap.EnsureAsync(Plan(), Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("storage_init_failed", result.Reason);
        Assert.Equal(DataRoot, seen![DataVolumeOptions.DataRootKey]);
        Assert.Equal("Development", seen[DataVolumeOptions.InstanceClassKey]);
        Assert.Equal(BootRoot, seen[DataVolumeOptions.BootRootKey]);
        Assert.Equal(["init", "--provider", "dev-plain"], seenArgs!);
    }

    [Fact]
    public void An_encrypting_provider_passes_its_container_options_to_init()
    {
        var args = StorageVolumeBootstrap.BuildArguments(new SetupStorageVolumeSection
        {
            Provider = "luks2",
            ContainerKind = "file",
            MaxGb = 64,
            ServiceAccount = "bazaar",
        });

        Assert.Equal(["init", "--provider", "luks2", "--container-kind", "file", "--max-gb", "64", "--service-account", "bazaar"], args);
    }
}
