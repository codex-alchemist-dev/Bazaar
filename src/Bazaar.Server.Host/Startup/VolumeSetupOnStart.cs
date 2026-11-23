using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Storage;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Creates the data volume a setup plan asks for before the host is built (doc 63 section 10, setup mechanics). The setup
/// apply runs with the database already up, so this is the only point where the volume can be created under the cluster.
/// It reads the plan named by <c>--setup-plan</c> or <c>BAZAAR_SETUP_PLAN</c>, the same file the apply reads later.
/// </summary>
/// <remarks>
/// A plan that cannot be read or is not valid is left to the apply, which reports it with the usual exit codes; nothing is
/// created from it. Wiring only: the work is in <see cref="StorageVolumeBootstrap"/>.
/// </remarks>
public static class VolumeSetupOnStart
{
    /// <param name="createBootstrap">A seam for tests; the default is the real bootstrap for this OS.</param>
    public static async Task<StorageBootstrapResult> RunAsync(
        IConfiguration configuration,
        TextWriter output,
        Func<IConfiguration, TextWriter, IStorageVolumeBootstrap>? createBootstrap = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);

        var notRequested = new StorageBootstrapResult(StorageBootstrapOutcome.NotRequested, 0, null, null);
        var planPath = SetupPlanStartOptions.FromConfiguration(configuration).PlanPath;
        if (planPath is null)
            return notRequested;

        string text;
        try
        {
            text = await File.ReadAllTextAsync(planPath, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return notRequested;
        }

        var loaded = SetupPlanLoader.Load(text, RegistrySetupSettingCatalog.Known);
        if (loaded.Plan is null || loaded.ExitCode == SetupExitCode.InvalidInput)
            return notRequested;

        var bootstrap = (createBootstrap ?? CreateDefault)(configuration, output);
        var result = await bootstrap.EnsureAsync(loaded.Plan, cancellationToken);
        switch (result.Outcome)
        {
            case StorageBootstrapOutcome.Refused:
                await output.WriteLineAsync($"setup storage refused: {result.Reason}: {result.Detail}".AsMemory(), cancellationToken);
                break;
            case StorageBootstrapOutcome.Created or StorageBootstrapOutcome.Unchanged:
                await output.WriteLineAsync($"setup storage: {result.Detail}".AsMemory(), cancellationToken);
                break;
        }

        return result;
    }

    private static StorageVolumeBootstrap CreateDefault(IConfiguration configuration, TextWriter output)
    {
        var (operators, providers) = VolumePlatform.Create();
        return StorageVolumeBootstrap.Create(configuration, operators, providers, () => KekComponent.OpenOrCreate(configuration), output);
    }
}
