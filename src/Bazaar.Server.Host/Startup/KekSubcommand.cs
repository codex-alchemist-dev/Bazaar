using Bazaar.Server.Hosting;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host kek rewrap --from file|dpapi</c>: moves every live GC person key and Soul DEK from the source KEK to the configured
/// <c>Bazaar:Identity:KekProvider</c> (doc 14). Run it with the host stopped, then start the host. The source key file is not
/// deleted. Returns 0 when every live row opens under the target, and 1 otherwise.
/// </summary>
public static class KekSubcommand
{
    private const string _usage = "usage: kek rewrap --from file|dpapi";

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .Build();

        if (args.Length == 0 || !args[0].Equals("rewrap", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(_usage, ct);
            return 1;
        }

        var from = Option(args[1..], "--from");
        if (from is null)
        {
            await output.WriteLineAsync(_usage, ct);
            return 1;
        }

        try
        {
            var host = configuration.GetSection(BazaarHostOptions.SectionName).Get<BazaarHostOptions>() ?? new BazaarHostOptions();
            var connectionString = await new BootstrapSecretResolver().ResolveAsync(host.DatabaseConnectionReference, ct);
            await using var dataSource = NpgsqlDataSource.Create(connectionString);

            var source = KekProviderFactory.OpenExisting(from, host.DataRoot);
            var target = KekProviderFactory.Create(configuration[KekComponent.KekProviderKey], host.DataRoot);
            try
            {
                var report = await KekRewriter.RunAsync(
                    source,
                    target,
                    [new PostgresKekRewrapStore(dataSource), new PostgresSoulDekRewrapStore(dataSource)],
                    new AuditLogSink(new PostgresAuditLog(dataSource)),
                    ct: ct);

                await output.WriteLineAsync(
                    $"Re-wrap {source.KekId} to {target.KekId}: scanned {report.Scanned}, moved {report.Rewrapped}, "
                    + $"already current {report.AlreadyCurrent}, conflicts {report.Conflicts}, failed {report.Failed.Count}.",
                    ct);
                if (!report.Succeeded)
                {
                    await output.WriteLineAsync("Not complete. Rerun the same command. Failed rows are listed by id in the audit log, never in key material.", ct);
                    return 1;
                }

                await output.WriteLineAsync("Complete. Start the host with identity.kek_provider set to the target. The source key file is kept until you remove it.", ct);
                return 0;
            }
            finally
            {
                (source as IDisposable)?.Dispose();
                (target as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or NotSupportedException or PlatformNotSupportedException or InvalidDataException)
        {
            await output.WriteLineAsync($"KEK re-wrap refused: {ex.Message}", ct);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    /// <summary>Adapts the host audit log to the re-wrap audit seam. Each entry records the event kind and counts only.</summary>
    private sealed class AuditLogSink(IAuditLog log) : IKekAuditSink
    {
        public Task RecordAsync(string kind, string payloadJson, CancellationToken ct) =>
            log.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "kek-rewrap", kind, payloadJson), ct);
    }
}
