using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Dev-only: writes the owner <c>human</c> row that the dev access-token issuer signs tokens for, so a local run can pair and
/// refresh without a manual insert. Runs only when <c>Bazaar:Remote:DevIssuer:Enabled</c> is true AND the host environment is
/// Development; in any other environment it does nothing. The insert is <c>ON CONFLICT DO NOTHING</c>, so repeat starts leave one
/// row, and an existing owner row (of any id) makes it a no-op too.
/// </summary>
public sealed class DevOwnerSeed(
    NpgsqlDataSource dataSource,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevOwnerSeed> logger) : IHostedService
{
    /// <summary>Fixed id of the dev owner. Not a real person; the dev issuer tokens name this id.</summary>
    public static readonly Guid OwnerId = new("3f0c7a52-6d1e-4b8a-9c4f-0d3e5a1b7e01");

    /// <summary>Display name of the dev owner row.</summary>
    public const string DisplayName = "Dev owner";

    /// <summary>The idempotent owner insert. Parameters: id, display name.</summary>
    public const string InsertSql = """
        INSERT INTO human (id, display_name, role, status)
        VALUES ($1, $2, 'owner', 'active')
        ON CONFLICT DO NOTHING
        """;

    /// <summary>True only when the dev issuer flag is on and the host runs in the Development environment.</summary>
    public static bool ShouldRun(IHostEnvironment environment, IConfiguration configuration) =>
        configuration.GetValue<bool>(RemoteAuthCompositionExtensions.DevIssuerEnabledKey) && environment.IsDevelopment();

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRun(environment, configuration))
        {
            return;
        }

        await SeedAsync(cancellationToken).ConfigureAwait(false);
        Log.Seeded(logger, OwnerId);
    }

    /// <summary>Inserts the dev owner row if it is missing. Safe to call any number of times.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(InsertSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = OwnerId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DisplayName });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static class Log
    {
        private static readonly Action<ILogger, Guid, Exception?> _seeded = LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(1, nameof(Seeded)),
            "Dev owner seed: owner human {OwnerId} is present (dev issuer only).");

        public static void Seeded(ILogger logger, Guid ownerId) => _seeded(logger, ownerId, null);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
