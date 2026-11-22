using Bazaar.Core;
using Npgsql;

namespace Bazaar.Server.Data.Migrations;

/// <summary>Logic behind the host's <c>migrate</c> subcommand (doc 03 WP4, doc 04). HOST1 wires it into <c>Program.cs</c>.</summary>
public static class MigrateCommand
{
    /// <summary>The provider folder for this OS: <c>windows</c> or <c>linux</c>.</summary>
    public static string CurrentProvider() => OperatingSystem.IsWindows() ? "windows" : "linux";

    /// <summary>
    /// Runs the migrations and writes a plain summary. <paramref name="options"/> carries <c>gc.partition_count</c> (default 16).
    /// <paramref name="components"/> orders the component namespaces. Returns 0 on success, 1 when a migration was refused or failed.
    /// </summary>
    public static async Task<int> RunAsync(string connectionString, IMigrationSource source, string provider, TextWriter output, MigrationOptions? options = null, IEnumerable<ComponentManifest>? components = null, CancellationToken ct = default)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var result = await new MigrationRunner(dataSource, options, components).MigrateAsync(source, provider, ct).ConfigureAwait(false);
            foreach (var m in result.Applied)
                await output.WriteLineAsync($"applied {m.Namespace}/{m.Version:D4}_{m.Name}", ct).ConfigureAwait(false);
            await output.WriteLineAsync($"done: {result.Applied.Count} applied, {result.AlreadyApplied} already applied", ct).ConfigureAwait(false);
            return 0;
        }
        catch (MigrationException ex)
        {
            await output.WriteLineAsync($"migrate refused: {ex.Message}", ct).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (TryDescribeDatabaseFailure(ex, out var line))
        {
            await output.WriteLineAsync($"migrate refused: {line}", ct).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// One safe line for a failure to reach or use the database (refused connection, bad credentials, malformed connection string,
    /// missing privilege, read-only session). Never echoes the connection string, and never a stack trace.
    /// </summary>
    public static bool TryDescribeDatabaseFailure(Exception ex, out string line)
    {
        switch (ex)
        {
            case PostgresException pg:
                line = $"the database refused the request: {pg.SqlState}: {FirstLine(pg.MessageText)}";
                return true;
            case NpgsqlException npg:
                line = $"cannot reach the database: {FirstLine(npg.InnerException?.Message ?? npg.Message)}";
                return true;
            case ArgumentException or FormatException:
                line = "the database connection string is not valid.";
                return true;
            default:
                line = string.Empty;
                return false;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
