using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// <c>bazaar-server storage init</c>, the one-time, explicit creation of the encrypted data volume (doc 63 sections 2, 3, 6, 8
/// and 10). This is the only place a volume is created: the host never creates a key, volume or cluster at start. It
/// refuses to touch an install that already has a descriptor, never deletes a container (that destroys data), clears every
/// key buffer, and never prints key material. It does not run <c>initdb</c>; the next setup step creates the cluster.
/// </summary>
public static class StorageCommand
{
    /// <summary>The operator or a step after it failed. What exists, and what to remove by hand, is on the output.</summary>
    public const int FailedExitCode = 1;

    private const string _usage =
        "usage: storage init --provider <kind> [--container <path>] [--container-kind file|device] [--max-gb <n>] [--service-account <name>] [--dry-run]";

    private const long _bytesPerGb = 1024L * 1024 * 1024;

    /// <param name="createOrOpenKek">Opens the KEK, creating it when absent. This command is setup, so unlike <c>volume open</c> it may create one.</param>
    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IEnumerable<IDataVolumeOperator> operators,
        IEnumerable<IDataVolumeProvider> providers,
        Func<IKekProvider> createOrOpenKek,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(createOrOpenKek);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || !string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(_usage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }

        var parsed = InitArguments.Parse(args.AsSpan(1));
        if (parsed.Error is not null)
        {
            await output.WriteLineAsync($"storage init: {parsed.Error}", CancellationToken.None);
            await output.WriteLineAsync(_usage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }

        try
        {
            var options = ReadOptions(configuration);
            var plan = BuildPlan(parsed, options, operators.ToList(), providers.ToList());
            if (parsed.DryRun)
            {
                await PrintDryRunAsync(plan, options, output);
                return 0;
            }

            return await InitAsync(plan, options, createOrOpenKek, output, cancellationToken);
        }
        catch (DataVolumeRefusedException e)
        {
            await output.WriteLineAsync($"storage init refused: {e.Code}: {OneLine(e.Message)}", CancellationToken.None);
            return DataVolumeRefusedException.ExitCode;
        }
        catch (ArgumentException e)
        {
            await output.WriteLineAsync($"storage init: {OneLine(e.Message)}", CancellationToken.None);
            await output.WriteLineAsync(_usage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }
    }

    private static async Task<int> InitAsync(
        InitPlan plan, DataVolumeOptions options, Func<IKekProvider> createOrOpenKek, TextWriter output, CancellationToken cancellationToken)
    {
        var kekPath = KekPath(options);
        var kekExisted = File.Exists(kekPath);
        var kek = OpenKek(createOrOpenKek);
        byte[]? key = null;
        var volumeId = plan.Plain ? Guid.CreateVersion7().ToString() : null;
        var containerMayExist = false;
        try
        {
            // An empty mount point the operator can mount on. A folder that already exists was checked to be empty.
            Directory.CreateDirectory(options.DataRoot);

            if (!plan.Plain)
            {
                key = VolumeKeyCustody.NewKey();
                var request = new DataVolumeCreateRequest(plan.ContainerKind, plan.ContainerPath, options.DataRoot, plan.MaxSizeBytes, plan.ServiceAccount);
                containerMayExist = true;
                volumeId = await plan.Operator!.CreateAsync(request, key, cancellationToken);
                if (string.IsNullOrWhiteSpace(volumeId))
                    throw new InvalidOperationException("The operator returned no volume id.");
            }

            var volume = new NewDataVolume(
                plan.InstallId, volumeId!, plan.ProviderKind, plan.Plain ? "none" : plan.ContainerKind,
                plan.Plain ? string.Empty : plan.ContainerPath, plan.Plain ? DataVolumeCiphers.None : DataVolumeCiphers.XtsAes256, options.InstanceClass);
            await DataVolumeInitializer.InitializeAsync(options, volume, key ?? [], kek, TimeProvider.System, cancellationToken);

            // The same checks the host makes at start, against what the OS now reports, before setup goes on.
            var checkOptions = new DataVolumeOptions
            {
                BootRoot = options.BootRoot,
                DataRoot = options.DataRoot,
                InstallId = plan.InstallId,
                VolumeId = volumeId!,
                InstanceClass = options.InstanceClass,
                IsDevelopmentEnvironment = plan.Plain,
            };
            await new DataVolumeGuard(checkOptions, kek, [plan.Provider]).VerifyAsync(cancellationToken);
        }
        catch (Exception e)
        {
            await ReportFailureAsync(e, plan, options, kekExisted, containerMayExist, volumeId, output);
            return FailedExitCode;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);

            (kek as IDisposable)?.Dispose();
        }

        await output.WriteLineAsync($"storage init: ok: volume {volumeId} provider {plan.ProviderKind}", CancellationToken.None);
        await output.WriteLineAsync($"set {DataVolumeOptions.InstallIdKey}={plan.InstallId} and {DataVolumeOptions.VolumeIdKey}={volumeId} in host config", CancellationToken.None);
        await output.WriteLineAsync("the PostgreSQL cluster is created by the next setup step; this command does not run initdb", CancellationToken.None);
        return 0;
    }

    /// <summary>States exactly what exists. Nothing is deleted: removing a container or a mounted volume destroys data and is the operator's call.</summary>
    private static async Task ReportFailureAsync(
        Exception error, InitPlan plan, DataVolumeOptions options, bool kekExisted, bool containerMayExist, string? volumeId, TextWriter output)
    {
        var descriptorWritten = File.Exists(options.DescriptorPath);
        var markerWritten = File.Exists(options.MarkerPath);
        await output.WriteLineAsync($"storage init failed: {error.GetType().Name}: {OneLine(error.Message)}", CancellationToken.None);

        if (containerMayExist)
        {
            await output.WriteLineAsync(
                volumeId is null
                    ? $"the volume operator failed; a {plan.ContainerKind} container may exist at '{plan.ContainerPath}' and may be mounted at '{options.DataRoot}'. Nothing was deleted."
                    : $"the volume was created (volume id {volumeId}) and may be mounted at '{options.DataRoot}'. Nothing was deleted.",
                CancellationToken.None);
            await output.WriteLineAsync(
                $"to start over, unmount and detach it by hand, remove the container '{plan.ContainerPath}' only if you are sure it holds no data you need, and remove any descriptor or marker listed below.",
                CancellationToken.None);
        }

        await output.WriteLineAsync($"descriptor: {(descriptorWritten ? $"written at '{options.DescriptorPath}'" : "not written")}", CancellationToken.None);
        await output.WriteLineAsync($"marker: {(markerWritten ? $"written at '{options.MarkerPath}'" : "not written")}", CancellationToken.None);

        if (!kekExisted && File.Exists(KekPath(options)) && !descriptorWritten)
            await output.WriteLineAsync($"the KEK created by this run is left in place at '{KekPath(options)}'; a later run reuses it.", CancellationToken.None);
    }

    private static async Task PrintDryRunAsync(InitPlan plan, DataVolumeOptions options, TextWriter output)
    {
        var kekPath = KekPath(options);
        await output.WriteLineAsync("storage init dry run: nothing is created", CancellationToken.None);
        await output.WriteLineAsync($"provider: {plan.ProviderKind}", CancellationToken.None);
        await output.WriteLineAsync($"instance class: {options.InstanceClass}", CancellationToken.None);
        await output.WriteLineAsync($"install id: {(plan.InstallIdGenerated ? "new id generated at run time" : plan.InstallId)}", CancellationToken.None);
        await output.WriteLineAsync($"boot root: {options.BootRoot}", CancellationToken.None);
        await output.WriteLineAsync($"data root (mount point): {options.DataRoot}", CancellationToken.None);
        if (plan.Plain)
        {
            await output.WriteLineAsync("container: none (plain folder, no encryption)", CancellationToken.None);
        }
        else
        {
            await output.WriteLineAsync($"container: {plan.ContainerKind} at {plan.ContainerPath}", CancellationToken.None);
            await output.WriteLineAsync($"max size: {(plan.ContainerKind == "file" ? $"{plan.MaxSizeBytes / _bytesPerGb} GB" : "the device")}", CancellationToken.None);
            await output.WriteLineAsync($"service account: {plan.ServiceAccount}", CancellationToken.None);
        }

        await output.WriteLineAsync($"cipher: {(plan.Plain ? DataVolumeCiphers.None : DataVolumeCiphers.XtsAes256)}", CancellationToken.None);
        await output.WriteLineAsync($"kek: {(File.Exists(kekPath) ? "exists, will be reused" : "will be created")} at {kekPath}", CancellationToken.None);
        await output.WriteLineAsync($"descriptor: {options.DescriptorPath}", CancellationToken.None);
        await output.WriteLineAsync($"marker: {options.MarkerPath}", CancellationToken.None);
    }

    private static DataVolumeOptions ReadOptions(IConfiguration configuration)
    {
        DataVolumeOptions options;
        try
        {
            options = DataVolumeComponent.ReadOptions(configuration);
        }
        catch (InvalidOperationException e)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, e.Message, e);
        }

        if (string.IsNullOrWhiteSpace(options.BootRoot) || string.IsNullOrWhiteSpace(options.DataRoot))
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.BootRootKey} and {DataVolumeOptions.DataRootKey} must both be set.");

        if (!Path.IsPathFullyQualified(options.BootRoot) || !Path.IsPathFullyQualified(options.DataRoot))
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.BootRootKey} and {DataVolumeOptions.DataRootKey} must be absolute paths.");

        if (IsInside(options.BootRoot, options.DataRoot) || IsInside(options.DataRoot, options.BootRoot))
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, "The boot root and the data root must not contain one another: the boot root is plain, the data root is the encrypted volume.");

        return options;
    }

    private static InitPlan BuildPlan(InitArguments args, DataVolumeOptions options, List<IDataVolumeOperator> operators, List<IDataVolumeProvider> providers)
    {
        if (File.Exists(options.DescriptorPath))
            throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, $"A volume descriptor already exists at '{options.DescriptorPath}'. storage init never overwrites or re-keys an install.");

        if (Directory.Exists(options.DataRoot) && Directory.EnumerateFileSystemEntries(options.DataRoot).Any())
            throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, $"The data root '{options.DataRoot}' is not empty. A new volume mounts on an empty folder; use storage migrate for existing data.");

        var kind = args.Provider!;
        var installIdGenerated = string.IsNullOrWhiteSpace(options.InstallId);
        var installId = installIdGenerated ? Guid.CreateVersion7().ToString() : options.InstallId;
        var provider = providers.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.Ordinal))
            ?? throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"The volume provider '{kind}' is not available on this host.");

        if (string.Equals(kind, DevPlainDataVolumeProvider.ProviderKind, StringComparison.Ordinal))
        {
            if (options.InstanceClass != InstanceClass.Development)
                throw new DataVolumeRefusedException(DataVolumeRefusal.NotEncrypted, "The dev-plain provider is accepted only for a development instance.");

            return new InitPlan(kind, provider, null, installId, installIdGenerated, "none", string.Empty, 0, string.Empty);
        }

        var volumeOperator = operators.FirstOrDefault(o => string.Equals(o.Kind, kind, StringComparison.Ordinal))
            ?? throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"The volume provider '{kind}' is not available on this host.");

        var containerPath = args.Container;
        if (args.ContainerKind == "file")
        {
            containerPath ??= Path.Combine(options.BootRoot, "volume", kind == "bitlocker-vhdx" ? "container.vhdx" : "container.img");
            if (args.MaxGb is null)
                throw new ArgumentException("--max-gb is required for a file container.");
        }

        if (string.IsNullOrWhiteSpace(containerPath))
            throw new ArgumentException("--container is required for a device container.");
        if (!Path.IsPathFullyQualified(containerPath))
            throw new ArgumentException("--container must be an absolute path.");
        if (string.IsNullOrWhiteSpace(args.ServiceAccount))
            throw new ArgumentException("--service-account is required.");

        if (args.ContainerKind == "file")
        {
            if (IsInside(options.DataRoot, containerPath))
                throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, "The container file must not be inside the data root it encrypts.");
            if (File.Exists(containerPath) || Directory.Exists(containerPath))
                throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, $"The container '{containerPath}' already exists. storage init never reuses or replaces a container.");
        }

        var maxBytes = args.ContainerKind == "file" ? args.MaxGb!.Value * _bytesPerGb : 0;
        return new InitPlan(kind, provider, volumeOperator, installId, installIdGenerated, args.ContainerKind, containerPath, maxBytes, args.ServiceAccount);
    }

    private static IKekProvider OpenKek(Func<IKekProvider> createOrOpenKek)
    {
        try
        {
            return createOrOpenKek();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, "The KEK could not be opened or created.", e);
        }
    }

    private static string KekPath(DataVolumeOptions options) => Path.Combine(options.BootRoot, "keys", "kek.bin");

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies below it.</summary>
    private static bool IsInside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string OneLine(string message) => message.ReplaceLineEndings(" ").Trim();

    private sealed record InitPlan(
        string ProviderKind,
        IDataVolumeProvider Provider,
        IDataVolumeOperator? Operator,
        string InstallId,
        bool InstallIdGenerated,
        string ContainerKind,
        string ContainerPath,
        long MaxSizeBytes,
        string ServiceAccount)
    {
        public bool Plain => Operator is null;
    }

    private sealed record InitArguments(string? Provider, string? Container, string ContainerKind, long? MaxGb, string? ServiceAccount, bool DryRun, string? Error)
    {
        public static InitArguments Parse(ReadOnlySpan<string> args)
        {
            string? provider = null, container = null, serviceAccount = null;
            var containerKind = "file";
            long? maxGb = null;
            var dryRun = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i].ToLowerInvariant();
                if (!seen.Add(option))
                    return Fail($"{args[i]} was given twice.");

                if (option == "--dry-run")
                {
                    dryRun = true;
                    continue;
                }

                if (option is not ("--provider" or "--container" or "--container-kind" or "--max-gb" or "--service-account"))
                    return Fail($"unknown option '{args[i]}'.");

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return Fail($"{args[i]} needs a value.");

                var value = args[++i];
                switch (option)
                {
                    case "--provider":
                        provider = value.Trim().ToLowerInvariant();
                        break;
                    case "--container":
                        container = value;
                        break;
                    case "--container-kind":
                        containerKind = value.Trim().ToLowerInvariant();
                        if (containerKind is not ("file" or "device"))
                            return Fail("--container-kind must be file or device.");
                        break;
                    case "--max-gb":
                        if (!long.TryParse(value, out var gb) || gb is < 1 or > 1_000_000)
                            return Fail("--max-gb must be a whole number of at least 1.");
                        maxGb = gb;
                        break;
                    default:
                        serviceAccount = value;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(provider))
                return Fail("--provider is required.");

            return new InitArguments(provider, container, containerKind, maxGb, serviceAccount, dryRun, null);
        }

        private static InitArguments Fail(string error) => new(null, null, "file", null, null, false, error);
    }
}
