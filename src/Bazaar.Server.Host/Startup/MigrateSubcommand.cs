using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host migrate</c> (doc 03 startup, doc 04). Reads the same host configuration as the service, takes the instance lock so it
/// cannot race a running host, applies pending migrations and exits. Returns 0 on success and 1 when refused or failed.
/// </summary>
public static class MigrateSubcommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .AddCommandLine(args)
            .Build();

        var options = configuration.GetSection(BazaarHostOptions.SectionName).Get<BazaarHostOptions>() ?? new BazaarHostOptions();
        var validation = new BazaarHostOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            await output.WriteLineAsync($"migrate refused: {string.Join(' ', validation.Failures)}", ct);
            return 1;
        }

        var services = new ServiceCollection().AddBazaarSecrets().BuildServiceProvider();
        var secrets = services.GetRequiredService<ISecretResolver>();

        string connectionString;
        try
        {
            connectionString = await secrets.ResolveAsync(options.DatabaseConnectionReference, ct);
        }
        catch (SecretResolutionException ex)
        {
            await output.WriteLineAsync($"migrate refused: {ex.Message}", ct);
            return 1;
        }

        await using var database = new DatabaseConnection(Microsoft.Extensions.Options.Options.Create(options), new SingleSecretResolver(options.DatabaseConnectionReference, connectionString));
        await using var instanceLock = new PostgresInstanceLock(database);
        if (!await instanceLock.TryAcquireAsync(ct))
        {
            await output.WriteLineAsync("migrate refused: another Bazaar.Server instance holds the instance lock; stop it first.", ct);
            return 1;
        }

        return await MigrateCommand.RunAsync(connectionString, new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), MigrateCommand.CurrentProvider(), output, ct: ct);
    }

    private sealed class SingleSecretResolver(string reference, string value) : ISecretResolver
    {
        public bool CanResolve(string candidate) => candidate == reference;

        public ValueTask<string> ResolveAsync(string candidate, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }
}
