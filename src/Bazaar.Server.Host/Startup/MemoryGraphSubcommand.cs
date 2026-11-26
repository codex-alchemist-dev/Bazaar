using System.Text.Json.Nodes;
using Bazaar.Server.Constellation;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Hosting;
using Bazaar.Server.PrivacyAudit;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host memory-graph doc-ids [--run --confirm]</c> (docs/design/constellation-memory-graph.md, "Convergence design", item 5):
/// the operator trigger of <see cref="MemoryGraphDocIdMigrator"/>. The migrator never runs on startup. Without flags it is a dry run that
/// prints the report and changes nothing. <c>--run</c> needs <c>--confirm</c> as well; the run then maps doc ids, prints what it did and
/// appends one <c>memory.graph.doc_ids.migrated</c> audit entry (counts and note ids only). Reads the same host configuration as the service
/// and refuses on a database whose schema is not current. Returns 0 on success and 1 when refused or failed. There is no admin panel
/// surface for maintenance yet; this follows the <c>migrate</c> subcommand.
/// </summary>
public static class MemoryGraphSubcommand
{
    /// <summary>The audit kind of a confirmed run.</summary>
    public const string RunAuditKind = "memory.graph.doc_ids.migrated";

    /// <summary>The audit actor: the operator at the host command line.</summary>
    public const string Actor = "operator:cli";

    /// <summary>The most note ids written to one audit entry; a larger run records the count only beyond this.</summary>
    public const int MaxAuditedNoteIds = 500;

    private const string _usage = "usage: memory-graph doc-ids [--run --confirm]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || !args[0].Equals("doc-ids", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(_usage, ct);
            return 1;
        }

        var flags = args[1..].Select(a => a.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        bool run = flags.Remove("--run");
        bool confirm = flags.Remove("--confirm");
        if (flags.Count > 0 || (confirm && !run))
        {
            await output.WriteLineAsync(_usage, ct);
            return 1;
        }

        if (run && !confirm)
        {
            await output.WriteLineAsync("memory-graph doc-ids --run refused: add --confirm to change data; run without flags for a dry run first.", ct);
            return 1;
        }

        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .Build();

        var options = configuration.GetSection(BazaarHostOptions.SectionName).Get<BazaarHostOptions>() ?? new BazaarHostOptions();
        var validation = new BazaarHostOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            await output.WriteLineAsync($"memory-graph refused: {string.Join(' ', validation.Failures)}", ct);
            return 1;
        }

        var secrets = new ServiceCollection().AddBazaarSecrets().BuildServiceProvider().GetRequiredService<ISecretResolver>();
        await using var database = new DatabaseConnection(Microsoft.Extensions.Options.Options.Create(options), secrets);
        try
        {
            await new MigrationSchemaGate(database).EnsureCurrentAsync(ct);
        }
        catch (SecretResolutionException ex)
        {
            await output.WriteLineAsync($"memory-graph refused: {ex.Message}", ct);
            return 1;
        }
        catch (SchemaVersionException ex)
        {
            await output.WriteLineAsync($"memory-graph refused: {ex.Message}", ct);
            return 1;
        }

        var dataSource = await database.GetAsync(ct);
        Guid? vaultId = Guid.TryParse(configuration[ConstellationComponent.VaultIdKey], out var parsed) ? parsed : null;
        var migrator = new MemoryGraphDocIdMigrator(dataSource, vaultId);

        if (!run)
        {
            await WriteReportAsync(await migrator.DryRunAsync(ct), output, ct);
            return 0;
        }

        var report = await migrator.RunAsync(ct);
        await WriteReportAsync(report, output, ct);
        await AuditAsync(new PostgresAuditLog(dataSource), report, ct);
        await output.WriteLineAsync($"Audited as {RunAuditKind}.", ct);
        return 0;
    }

    /// <summary>Appends the audit entry of a confirmed run: counts and mapped note ids, never paths or titles.</summary>
    internal static Task AuditAsync(IAuditLog log, MemoryDocIdReport report, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["mapped"] = report.Mappings.Count,
            ["rows_updated"] = report.RowsUpdated,
            ["unmapped"] = report.Unmapped.Count,
            ["conflicts"] = report.Conflicts.Count,
            ["already_mapped"] = report.AlreadyMapped,
            ["note_ids"] = new JsonArray([.. report.Mappings.Take(MaxAuditedNoteIds).Select(m => (JsonNode?)JsonValue.Create(m.NoteId))]),
            ["note_ids_truncated"] = report.Mappings.Count > MaxAuditedNoteIds,
        };
        AuditPayloadGuard.Ensure(payload);
        return log.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, Actor, RunAuditKind, payload.ToJsonString()), ct);
    }

    /// <summary>One summary line, then one line per note: mapped notes with their doc id, unmapped and conflicting notes with the reason.</summary>
    private static async Task WriteReportAsync(MemoryDocIdReport report, TextWriter output, CancellationToken ct)
    {
        await output.WriteLineAsync(
            $"{(report.DryRun ? "dry run" : "run")}: {report.Mappings.Count} to map, {report.Unmapped.Count} unmapped, " +
            $"{report.Conflicts.Count} conflicts, {report.AlreadyMapped} already mapped, {report.RowsUpdated} rows updated.",
            ct);
        foreach (var mapping in report.Mappings)
        {
            await output.WriteLineAsync($"  map {mapping.NoteId} -> {mapping.DocId:D} ({mapping.Rows} rows)", ct);
        }

        foreach (var issue in report.Unmapped)
        {
            await output.WriteLineAsync($"  unmapped {issue.NoteId}: {issue.Reason}", ct);
        }

        foreach (var issue in report.Conflicts)
        {
            await output.WriteLineAsync($"  conflict {issue.NoteId}: {issue.Reason}", ct);
        }
    }
}
