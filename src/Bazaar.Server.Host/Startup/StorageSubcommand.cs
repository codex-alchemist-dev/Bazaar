using Bazaar.Components.ManagedPostgres;
using Bazaar.Server.DataVolume;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host storage init|migrate</c> (doc 63 section 10). Wiring only: it reads the same host configuration as the
/// service and hands the operators and providers of this OS to <see cref="StorageCommand"/>. Unlike the host and
/// <c>volume</c>, this command may create the KEK, because it is setup.
/// </summary>
public static class StorageSubcommand
{
    public static Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .Build();

        var (operators, providers) = VolumePlatform.Create();

        // migrate (doc 63 section 10): the shipped probe watches the old cluster's postmaster.pid, and pg_checksums comes from the bundle.
        var bundleRoot = configuration.GetSection(ManagedPostgresOptions.SectionName).Get<ManagedPostgresOptions>()?.BundleRoot;
        var migrate = new StorageMigrateServices(new PostmasterPidHostRunningProbe(), new PgChecksumsProcessVerifier(bundleRoot));
        return StorageCommand.RunAsync(args, configuration, operators, providers, () => KekComponent.OpenOrCreate(configuration), migrate, output, ct);
    }
}
