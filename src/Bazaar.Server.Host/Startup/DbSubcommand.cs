using Bazaar.Components.ManagedPostgres;
using Bazaar.Components.ManagedPostgres.Maintenance;
using Bazaar.Components.ManagedPostgres.Secrets;
using Bazaar.Server.Providers.Windows.Postgres;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host db backup [--keep N]</c> and <c>Bazaar.Server.Host db upgrade --to N [--keep N]</c>. The commands read the
/// same configuration as the service. They refuse while the host runs the cluster (see <see cref="ManagedPostgresMaintenance"/>).
/// Returns 0 on success and 1 when refused or failed.
/// </summary>
public static class DbSubcommand
{
    private const string _usage = "usage: db backup [--keep N] | db upgrade --to N [--keep N]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .Build();

        var settings = configuration.GetSection(ManagedPostgresOptions.SectionName).Get<ManagedPostgresOptions>() ?? new ManagedPostgresOptions();
        if (args.Length == 0)
        {
            await output.WriteLineAsync(_usage, ct);
            return 1;
        }

        try
        {
            var maintenance = new ManagedPostgresMaintenance(
                Options.Create(settings),
                new ProcessPostgresCommandRunner(),
                PasswordStore(settings),
                TimeProvider.System);

            switch (args[0].ToLowerInvariant())
            {
                case "backup":
                {
                    var keep = IntOption(args[1..], "--keep") ?? settings.Backup.KeepDumps;
                    var backup = await maintenance.BackupAsync(keep, ct);
                    await output.WriteLineAsync($"Backup written: {backup.DumpPath} ({backup.Bytes} bytes), sha256 {backup.Sha256}.", ct);
                    return 0;
                }

                case "upgrade":
                {
                    var to = IntOption(args[1..], "--to") ?? throw new PgMaintenanceException("db upgrade needs --to <major>.");
                    var keep = IntOption(args[1..], "--keep") ?? settings.Backup.KeepDumps;
                    var result = await maintenance.UpgradeAsync(to, keep, ct);
                    await output.WriteLineAsync(
                        $"Upgraded PostgreSQL {result.FromMajor} to {result.ToMajor}. Restore point: {result.Backup.DumpPath}. " +
                        $"Set Bazaar:Database:Major to {result.ToMajor}, then start the host.",
                        ct);
                    return 0;
                }

                default:
                    await output.WriteLineAsync(_usage, ct);
                    return 1;
            }
        }
        catch (PgMaintenanceException ex)
        {
            await output.WriteLineAsync($"db {args[0]} refused: {ex.Message}", ct);
            return 1;
        }
    }

    /// <summary>Windows keeps the password under DPAPI for the current account; Linux keeps it AES-GCM encrypted under the store key.</summary>
    private static IManagedPasswordStore PasswordStore(ManagedPostgresOptions settings) =>
        OperatingSystem.IsWindows()
            ? new DpapiManagedPasswordStore(settings.ResolvedPasswordStorePath)
            : new AesGcmManagedPasswordStore(settings.ResolvedPasswordStorePath, new LinuxManagedStoreKeyProvider());

    private static int? IntOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
                {
                    throw new PgMaintenanceException($"{name} needs a whole number of at least 1.");
                }

                return value;
            }
        }

        return null;
    }
}
