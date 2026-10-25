using Npgsql;

namespace Bazaar.Server.Data.Migrations;

/// <summary>Logic behind the host's <c>migrate</c> subcommand (doc 03 WP4, doc 04). HOST1 wires it into <c>Program.cs</c>.</summary>
public static class MigrateCommand
{
    /// <summary>The provider folder for this OS: <c>windows</c> or <c>linux</c>.</summary>
    public static string CurrentProvider() => OperatingSystem.IsWindows() ? "windows" : "linux";

    /// <summary>Runs the migrations and writes a plain summary. Returns 0 on success, 1 when a migration was refused or failed.</summary>
    public static async Task<int> RunAsync(string connectionString, IMigrationSource source, string provider, TextWriter output, CancellationToken ct = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            var result = await new MigrationRunner(dataSource).MigrateAsync(source, provider, ct).ConfigureAwait(false);
            foreach (var m in result.Applied)
                await output.WriteLineAsync($"applied {m.Provider}/{m.Version:D4}_{m.Name}", ct).ConfigureAwait(false);
            await output.WriteLineAsync($"done: {result.Applied.Count} applied, {result.AlreadyApplied} already applied", ct).ConfigureAwait(false);
            return 0;
        }
        catch (MigrationException ex)
        {
            await output.WriteLineAsync($"migrate refused: {ex.Message}", ct).ConfigureAwait(false);
            return 1;
        }
    }
}
