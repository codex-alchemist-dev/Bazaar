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

    /// <summary>The usage line of <c>storage migrate</c>.</summary>
    internal const string MigrateUsage =
        "usage: storage migrate --from <old plain data root> --provider <kind> [--container <path>] [--container-kind file|device] [--max-gb <n>] [--service-account <name>] [--dry-run]";

    private const long _bytesPerGb = 1024L * 1024 * 1024;

    /// <param name="createOrOpenKek">Opens the KEK, creating it when absent. This command is setup, so unlike <c>volume open</c> it may create one.</param>
    public static Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IEnumerable<IDataVolumeOperator> operators,
        IEnumerable<IDataVolumeProvider> providers,
        Func<IKekProvider> createOrOpenKek,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, configuration, operators, providers, createOrOpenKek, migrate: null, output, cancellationToken);

    /// <param name="migrate">What <c>storage migrate</c> needs; without it that verb is not available.</param>
    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IEnumerable<IDataVolumeOperator> operators,
        IEnumerable<IDataVolumeProvider> providers,
        Func<IKekProvider> createOrOpenKek,
        StorageMigrateServices? migrate,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(createOrOpenKek);
        ArgumentNullException.ThrowIfNull(output);

        var verb = args.Length == 0 ? string.Empty : args[0].ToLowerInvariant();
        if (verb is not ("init" or "migrate"))
        {
            await output.WriteLineAsync(_usage, CancellationToken.None);
            await output.WriteLineAsync(MigrateUsage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }

        var isMigrate = verb == "migrate";
        var usage = isMigrate ? MigrateUsage : _usage;
        var parsed = StorageArguments.Parse(args.AsSpan(1), isMigrate);
        if (parsed.Error is not null)
        {
            await output.WriteLineAsync($"storage {verb}: {parsed.Error}", CancellationToken.None);
            await output.WriteLineAsync(usage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }

        if (isMigrate && migrate is null)
        {
            await output.WriteLineAsync("storage migrate: not available in this host (no migration services were provided).", CancellationToken.None);
            return FailedExitCode;
        }

        try
        {
            var options = ReadOptions(configuration);
            if (isMigrate)
                return await StorageMigrateCommand.RunAsync(parsed, options, operators.ToList(), providers.ToList(), createOrOpenKek, migrate!, output, cancellationToken);

            var plan = BuildPlan(parsed, options, operators.ToList(), providers.ToList(), resuming: false);
            if (parsed.DryRun)
            {
                await PrintDryRunAsync(plan, options, output);
                return 0;
            }

            return await InitAsync(plan, options, createOrOpenKek, output, cancellationToken);
        }
        catch (DataVolumeRefusedException e)
        {
            await output.WriteLineAsync($"storage {verb} refused: {e.Code}: {OneLine(e.Message)}", CancellationToken.None);
            return DataVolumeRefusedException.ExitCode;
        }
        catch (ArgumentException e)
        {
            await output.WriteLineAsync($"storage {verb}: {OneLine(e.Message)}", CancellationToken.None);
            await output.WriteLineAsync(usage, CancellationToken.None);
            return VolumeCommand.UsageExitCode;
        }
    }

    private static async Task<int> InitAsync(
        InitPlan plan, DataVolumeOptions options, Func<IKekProvider> createOrOpenKek, TextWriter output, CancellationToken cancellationToken)
    {
        var kekExisted = File.Exists(KekPath(options));
        var kek = OpenKek(createOrOpenKek);
        var state = new VolumeCreationState();
        try
        {
            await CreateVolumeAsync(plan, options, kek, state, beforeOperatorCreate: null, cancellationToken);
        }
        catch (Exception e)
        {
            await ReportFailureAsync(e, plan, options, kekExisted, state.ContainerMayExist, state.VolumeId, output);
            return FailedExitCode;
        }
        finally
        {
            (kek as IDisposable)?.Dispose();
        }

        var volumeId = state.VolumeId;
        await output.WriteLineAsync($"storage init: ok: volume {volumeId} provider {plan.ProviderKind}", CancellationToken.None);
        await output.WriteLineAsync($"set {DataVolumeOptions.InstallIdKey}={plan.InstallId} and {DataVolumeOptions.VolumeIdKey}={volumeId} in host config", CancellationToken.None);
        await output.WriteLineAsync("the PostgreSQL cluster is created by the next setup step; this command does not run initdb", CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// Creates the volume and writes the descriptor and marker, then runs the guard checks (shared by <c>init</c> and <c>migrate</c>).
    /// Clears the volume key buffer on every path. <paramref name="beforeOperatorCreate"/> runs immediately before the operator
    /// is called, so a caller can journal the intent: after it, a container may exist that this code never deletes.
    /// </summary>
    internal static async Task CreateVolumeAsync(
        InitPlan plan, DataVolumeOptions options, IKekProvider kek, VolumeCreationState state,
        Func<CancellationToken, ValueTask>? beforeOperatorCreate, CancellationToken cancellationToken)
    {
        byte[]? key = null;
        state.VolumeId = plan.Plain ? Guid.CreateVersion7().ToString() : null;
        try
        {
            // An empty mount point the operator can mount on. A folder that already exists was checked to be empty.
            Directory.CreateDirectory(options.DataRoot);

            if (!plan.Plain)
            {
                key = VolumeKeyCustody.NewKey();
                var request = new DataVolumeCreateRequest(plan.ContainerKind, plan.ContainerPath, options.DataRoot, plan.MaxSizeBytes, plan.ServiceAccount);
                if (beforeOperatorCreate is not null)
                    await beforeOperatorCreate(cancellationToken);

                state.ContainerMayExist = true;
                state.VolumeId = await plan.Operator!.CreateAsync(request, key, cancellationToken);
                if (string.IsNullOrWhiteSpace(state.VolumeId))
                    throw new InvalidOperationException("The operator returned no volume id.");
            }

            var volume = new NewDataVolume(
                plan.InstallId, state.VolumeId!, plan.ProviderKind, plan.Plain ? "none" : plan.ContainerKind,
                plan.Plain ? string.Empty : plan.ContainerPath, plan.Plain ? DataVolumeCiphers.None : DataVolumeCiphers.XtsAes256, options.InstanceClass);
            await DataVolumeInitializer.InitializeAsync(options, volume, key ?? [], kek, TimeProvider.System, cancellationToken);
            await VerifyGuardAsync(plan, options, kek, state.VolumeId!, cancellationToken);
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The same checks the host makes at start, against what the OS now reports.</summary>
    internal static async Task VerifyGuardAsync(InitPlan plan, DataVolumeOptions options, IKekProvider kek, string volumeId, CancellationToken cancellationToken)
    {
        var checkOptions = new DataVolumeOptions
        {
            BootRoot = options.BootRoot,
            DataRoot = options.DataRoot,
            InstallId = plan.InstallId,
            VolumeId = volumeId,
            InstanceClass = options.InstanceClass,
            IsDevelopmentEnvironment = plan.Plain,
        };
        await new DataVolumeGuard(checkOptions, kek, [plan.Provider]).VerifyAsync(cancellationToken);
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
        await output.WriteLineAsync(File.Exists(kekPath) ? $"kek: exists, will be reused at {kekPath}" : $"kek: will be created under {Path.GetDirectoryName(kekPath)}", CancellationToken.None);
        await output.WriteLineAsync($"descriptor: {options.DescriptorPath}", CancellationToken.None);
        await output.WriteLineAsync($"marker: {options.MarkerPath}", CancellationToken.None);
    }

    internal static DataVolumeOptions ReadOptions(IConfiguration configuration)
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

    /// <param name="resuming">A resumed migration: the descriptor, the mount point's content and the container are its own earlier work, not obstacles.</param>
    internal static InitPlan BuildPlan(
        StorageArguments args, DataVolumeOptions options, List<IDataVolumeOperator> operators, List<IDataVolumeProvider> providers, bool resuming)
    {
        if (!resuming && File.Exists(options.DescriptorPath))
            throw new DataVolumeRefusedException(DataVolumeRefusal.AlreadyInitialised, $"A volume descriptor already exists at '{options.DescriptorPath}'. storage init never overwrites or re-keys an install.");

        if (!resuming && Directory.Exists(options.DataRoot) && Directory.EnumerateFileSystemEntries(options.DataRoot).Any())
            throw new DataVolumeRefusedException(DataVolumeRefusal.AlreadyInitialised, $"The data root '{options.DataRoot}' is not empty. A new volume mounts on an empty folder; use storage migrate for existing data.");

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
            if (!resuming && (File.Exists(containerPath) || Directory.Exists(containerPath)))
                throw new DataVolumeRefusedException(DataVolumeRefusal.AlreadyInitialised, $"The container '{containerPath}' already exists. storage init never reuses or replaces a container.");
        }

        var maxBytes = args.ContainerKind == "file" ? args.MaxGb!.Value * _bytesPerGb : 0;
        return new InitPlan(kind, provider, volumeOperator, installId, installIdGenerated, args.ContainerKind, containerPath, maxBytes, args.ServiceAccount);
    }

    internal static IKekProvider OpenKek(Func<IKekProvider> createOrOpenKek)
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

    /// <summary>
    /// The host KEK files under <c>keys/</c>: the <c>file</c> provider's and the <c>dpapi</c> provider's (doc 14). Each provider keeps
    /// its own file, so both can be present while a provider switch re-wraps.
    /// </summary>
    internal static IReadOnlyList<string> KekFileNames { get; } = ["kek.bin", "kek-dpapi.bin"];

    /// <summary>True when <paramref name="name"/> is one of <see cref="KekFileNames"/>.</summary>
    internal static bool IsKekFileName(string name) => KekFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The KEK file at the boot root: the one that exists, or the <c>file</c> provider's path when none does yet.</summary>
    internal static string KekPath(DataVolumeOptions options)
    {
        var keys = Path.Combine(options.BootRoot, "keys");
        return KekFileNames.Select(n => Path.Combine(keys, n)).FirstOrDefault(File.Exists) ?? Path.Combine(keys, KekFileNames[0]);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies below it.</summary>
    internal static bool IsInside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    internal static string OneLine(string message) => message.ReplaceLineEndings(" ").Trim();
}
