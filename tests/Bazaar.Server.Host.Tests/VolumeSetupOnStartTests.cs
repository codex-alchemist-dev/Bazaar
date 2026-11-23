using System.Text.Json;
using Bazaar.Server.DataVolume;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Hosting;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host side of the plan's storage section (doc 63 section 10): the volume is created before the host is built, and the guard is on only with a boot root.</summary>
public sealed class VolumeSetupOnStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-volume-start-" + Guid.NewGuid().ToString("N"));

    public VolumeSetupOnStartTests() => Directory.CreateDirectory(_root);

    private string BootRoot => Path.Combine(_root, "boot");

    private string DataRoot => Path.Combine(_root, "data");

    private string PlanPath => Path.Combine(_root, "plan.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IConfigurationBuilder Config(bool withBootRoot = true, bool withPlan = true)
    {
        var values = new Dictionary<string, string?>();
        if (withBootRoot) values[DataVolumeOptions.BootRootKey] = BootRoot;
        if (withPlan) values[SetupPlanStartOptions.PlanKey] = PlanPath;
        return new ConfigurationBuilder().AddInMemoryCollection(values);
    }

    private void WritePlan(string storage) => File.WriteAllText(PlanPath, $$"""
        {
          "schemaVersion": 1,
          "install": {
            "companyName": "Example Studio",
            "companySlug": "example-studio",
            "locale": "en",
            "timezone": "Europe/London",
            "releaseChannel": "beta",
            "updatePolicy": { "security": "automatic", "other": "manual" }
          },
          "database": { "postgresMajor": 18, "dataRoot": {{JsonSerializer.Serialize(DataRoot)}}, "gcPartitionCount": 16 },
          {{storage}}
          "custody": {
            "kekProvider": "dpapi",
            "kekRef": "dpapi:kek.bin",
            "identityKek": "file",
            "auditAnchorTarget": "https://anchor.example.invalid/bazaar-audit",
            "mergeCredential": { "kind": "owner_app", "appIdRef": "secret://bazaar/owner/app-id" }
          },
          "owner": {
            "displayName": "Owner",
            "requireSecondFactor": true,
            "recovery": { "printedCodes": false, "backupOwners": [] }
          },
          "privacy": { "profile": "private" },
          "backups": { "target": "file:/mnt/backup/bazaar", "drillIntervalDays": 30, "acknowledgeNoTarget": false },
          "models": { "sources": [], "defaultPreset": "free-lane" },
          "email": { "enabled": false },
          "scamMailProtocol": { "enabled": false },
          "company": { "source": "template:starter@1.0.0" },
          "modules": [],
          "network": { "statusBind": "loopback", "statusPort": 8443 },
          "settings": {}
        }
        """);

    private const string _devPlain = "\"storage\": { \"instanceClass\": \"development\", \"volume\": { \"provider\": \"dev-plain\" } },";

    [Fact]
    public async Task No_plan_asks_for_nothing_and_the_host_may_start()
    {
        var result = await VolumeSetupOnStart.RunAsync(Config(withPlan: false).Build(), TextWriter.Null, (_, _) => throw new InvalidOperationException("not expected"), Ct);

        Assert.Equal(StorageBootstrapOutcome.NotRequested, result.Outcome);
        Assert.True(result.MayStart);
    }

    [Fact]
    public async Task A_plan_that_cannot_be_read_or_is_not_valid_is_left_to_the_apply()
    {
        var missing = await VolumeSetupOnStart.RunAsync(Config().Build(), TextWriter.Null, (_, _) => throw new InvalidOperationException("not expected"), Ct);
        File.WriteAllText(PlanPath, "{ not json");
        var broken = await VolumeSetupOnStart.RunAsync(Config().Build(), TextWriter.Null, (_, _) => throw new InvalidOperationException("not expected"), Ct);

        Assert.Equal(StorageBootstrapOutcome.NotRequested, missing.Outcome);
        Assert.Equal(StorageBootstrapOutcome.NotRequested, broken.Outcome);
        Assert.False(Directory.Exists(BootRoot));
    }

    [Fact]
    public async Task A_plan_without_a_storage_section_creates_nothing()
    {
        WritePlan("");

        var result = await VolumeSetupOnStart.RunAsync(Config().Build(), TextWriter.Null, cancellationToken: Ct);

        Assert.Equal(StorageBootstrapOutcome.NotRequested, result.Outcome);
        Assert.False(Directory.Exists(BootRoot));
    }

    [Fact]
    public async Task A_refusal_is_printed_and_stops_the_start_with_its_exit_code()
    {
        WritePlan(_devPlain);
        var output = new StringWriter();
        var refused = new StorageBootstrapResult(StorageBootstrapOutcome.Refused, 78, "data_volume_already_initialised", "A volume exists.");

        var result = await VolumeSetupOnStart.RunAsync(Config().Build(), output, (_, _) => new FixedBootstrap(refused), Ct);

        Assert.False(result.MayStart);
        Assert.Equal(78, result.ExitCode);
        Assert.Contains("setup storage refused: data_volume_already_initialised: A volume exists.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_storage_section_without_a_boot_root_is_refused()
    {
        WritePlan(_devPlain);

        var result = await VolumeSetupOnStart.RunAsync(Config(withBootRoot: false).Build(), TextWriter.Null, cancellationToken: Ct);

        Assert.Equal(StorageBootstrapOutcome.Refused, result.Outcome);
        Assert.Equal(78, result.ExitCode);
    }

    [Fact]
    public async Task A_dev_plain_plan_creates_the_volume_and_the_host_config_supplies_its_values()
    {
        WritePlan(_devPlain);
        var builder = Config();

        var first = await VolumeSetupOnStart.RunAsync(builder.Build(), TextWriter.Null, cancellationToken: Ct);
        var second = await VolumeSetupOnStart.RunAsync(builder.Build(), TextWriter.Null, cancellationToken: Ct);
        var configuration = builder.AddVolumeHostConfig().Build();

        Assert.Equal(StorageBootstrapOutcome.Created, first.Outcome);
        Assert.Equal(StorageBootstrapOutcome.Unchanged, second.Outcome);
        Assert.True(File.Exists(Path.Combine(BootRoot, "volume", "descriptor.json")));
        Assert.Equal(DataRoot, configuration[$"{BazaarHostOptions.SectionName}:DataRoot"]);
        Assert.False(string.IsNullOrWhiteSpace(configuration[DataVolumeOptions.InstallIdKey]));
        Assert.False(string.IsNullOrWhiteSpace(configuration[DataVolumeOptions.VolumeIdKey]));
        Assert.Equal("Development", configuration[DataVolumeOptions.InstanceClassKey]);
        Assert.Equal("file", configuration["Bazaar:Identity:KekProvider"]);
    }

    [Fact]
    public void Without_a_boot_root_the_volume_host_file_is_not_added()
    {
        var builder = Config(withBootRoot: false, withPlan: false);
        var before = builder.Sources.Count;

        builder.AddVolumeHostConfig();

        Assert.Equal(before, builder.Sources.Count);
    }

    [Fact]
    public void The_guard_and_the_setup_step_are_registered_only_with_a_boot_root()
    {
        var without = new ServiceCollection();
        without.AddBazaarDataVolume(Config(withBootRoot: false, withPlan: false).Build());

        var with = new ServiceCollection();
        with.AddBazaarDataVolume(Config(withPlan: false).Build());

        Assert.DoesNotContain(without, d => d.ServiceType == typeof(IDataVolumeGuard) || d.ServiceType == typeof(ISetupStep));
        Assert.Contains(with, d => d.ServiceType == typeof(IDataVolumeGuard));
        Assert.Contains(with, d => d.ServiceType == typeof(ISetupStep));
        Assert.Contains(with, d => d.ServiceType == typeof(IDataVolumeProvider));
    }

    private sealed class FixedBootstrap(StorageBootstrapResult result) : IStorageVolumeBootstrap
    {
        public Task<StorageBootstrapResult> EnsureAsync(SetupPlan plan, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
