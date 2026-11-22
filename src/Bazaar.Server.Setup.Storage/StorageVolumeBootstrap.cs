using System.Globalization;
using Bazaar.Server.DataVolume;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Setup.Storage;

/// <summary>Runs <c>storage init</c> with the given arguments and configuration, writing its lines to the writer. Returns its exit code.</summary>
public delegate Task<int> StorageInitRunner(string[] args, IConfiguration configuration, TextWriter output, CancellationToken cancellationToken);

/// <summary>
/// Creates the data volume a setup plan asks for before the host is built (doc 63 section 10, setup mechanics). It uses the
/// same code as <c>storage init</c>, so the refusals are the same: a descriptor, a container or a non-empty data root is
/// never overwritten. Afterwards it writes <see cref="VolumeHostConfigFile"/> so the host knows the volume it must find.
/// </summary>
public sealed class StorageVolumeBootstrap(IConfiguration configuration, StorageInitRunner runInit, TextWriter output) : IStorageVolumeBootstrap
{
    /// <summary>The bootstrap over the real <c>storage init</c> for this host's operators and providers.</summary>
    public static StorageVolumeBootstrap Create(
        IConfiguration configuration,
        IEnumerable<IDataVolumeOperator> operators,
        IEnumerable<IDataVolumeProvider> providers,
        Func<IKekProvider> createOrOpenKek,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(createOrOpenKek);
        var operatorList = operators.ToList();
        var providerList = providers.ToList();
        return new StorageVolumeBootstrap(
            configuration,
            (args, config, writer, token) => StorageCommand.RunAsync(args, config, operatorList, providerList, createOrOpenKek, writer, token),
            output);
    }

    /// <inheritdoc />
    public async Task<StorageBootstrapResult> EnsureAsync(SetupPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var volume = plan.Storage.Volume;
        if (string.IsNullOrWhiteSpace(volume.Provider))
            return new StorageBootstrapResult(StorageBootstrapOutcome.NotRequested, 0, null, null);

        var bootRoot = configuration[DataVolumeOptions.BootRootKey];
        if (string.IsNullOrWhiteSpace(bootRoot) || !Path.IsPathFullyQualified(bootRoot))
            return Refuse(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.BootRootKey} must be set to an absolute path before a volume can be created.");

        var dataRoot = plan.Database.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
            return Refuse(DataVolumeRefusal.NotInitialised, "database.dataRoot must be an absolute path: the volume is mounted there.");

        if (!Enum.TryParse<InstanceClass>(plan.Storage.InstanceClass, ignoreCase: true, out var instanceClass))
            return Refuse(DataVolumeRefusal.NotInitialised, "storage.instanceClass must be production or development.");

        var descriptorPath = Path.Combine(bootRoot, "volume", "descriptor.json");
        DataVolumeDescriptor? existing;
        try
        {
            existing = await DescriptorStore.ReadAsync(descriptorPath, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Refuse(DataVolumeRefusal.IdentityMismatch, $"The volume descriptor at '{descriptorPath}' is damaged. Nothing was changed.");
        }

        if (existing is not null)
        {
            if (!string.Equals(existing.ProviderKind, volume.Provider, StringComparison.Ordinal) || existing.InstanceClass != instanceClass)
                return Refuse(DataVolumeRefusal.AlreadyInitialised, "A volume already exists for this install with another provider or instance class than the plan names. A volume is never re-created.");

            var configuredDataRoot = configuration[DataVolumeOptions.DataRootKey];
            if (File.Exists(VolumeHostConfigFile.PathFor(bootRoot)) && !string.IsNullOrWhiteSpace(configuredDataRoot) && !SamePath(configuredDataRoot, dataRoot))
                return Refuse(DataVolumeRefusal.AlreadyInitialised, "The existing volume is mounted at another data root than the plan names. A volume is never moved by setup.");

            await VolumeHostConfigFile.WriteAsync(bootRoot, dataRoot, existing, cancellationToken);
            return new StorageBootstrapResult(StorageBootstrapOutcome.Unchanged, 0, null, $"volume {existing.VolumeId} already exists");
        }

        var overlay = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataVolumeOptions.DataRootKey] = dataRoot,
                [DataVolumeOptions.InstanceClassKey] = instanceClass.ToString(),
            })
            .Build();

        var exitCode = await runInit(BuildArguments(volume), overlay, output, cancellationToken);
        if (exitCode != 0)
        {
            var reason = exitCode == DataVolumeRefusedException.ExitCode ? "data_volume_refused" : "storage_init_failed";
            return new StorageBootstrapResult(StorageBootstrapOutcome.Refused, exitCode, reason, "storage init did not create the volume; its lines above say why.");
        }

        var created = await DescriptorStore.ReadAsync(descriptorPath, cancellationToken);
        if (created is null)
            return new StorageBootstrapResult(StorageBootstrapOutcome.Refused, StorageCommand.FailedExitCode, "storage_init_failed", "storage init reported success but wrote no descriptor.");

        await VolumeHostConfigFile.WriteAsync(bootRoot, dataRoot, created, cancellationToken);
        return new StorageBootstrapResult(StorageBootstrapOutcome.Created, 0, null, $"volume {created.VolumeId} created");
    }

    /// <summary>The <c>storage init</c> arguments for a plan's volume section. Values are passed as given; init validates them.</summary>
    public static string[] BuildArguments(SetupStorageVolumeSection volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var args = new List<string> { "init", "--provider", volume.Provider };
        if (string.Equals(volume.Provider, DevPlainDataVolumeProvider.ProviderKind, StringComparison.Ordinal))
            return [.. args];

        args.AddRange(["--container-kind", volume.ContainerKind]);
        if (!string.IsNullOrWhiteSpace(volume.Container))
            args.AddRange(["--container", volume.Container]);
        if (volume.MaxGb is { } maxGb)
            args.AddRange(["--max-gb", maxGb.ToString(CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(volume.ServiceAccount))
            args.AddRange(["--service-account", volume.ServiceAccount]);
        return [.. args];
    }

    private static StorageBootstrapResult Refuse(DataVolumeRefusal reason, string detail) =>
        new(StorageBootstrapOutcome.Refused, DataVolumeRefusedException.ExitCode, new DataVolumeRefusedException(reason, detail).Code, detail);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
