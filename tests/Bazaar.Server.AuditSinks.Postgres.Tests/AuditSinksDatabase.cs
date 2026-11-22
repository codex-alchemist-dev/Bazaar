using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.AuditSinks.Postgres;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.AuditSinks.Postgres.Tests;

/// <summary>
/// A migrated test database holding the chain (common 0004, which also creates <c>bazaar_app</c>), the checkpoints (audit 0001)
/// and this module's <c>Sql/components/auditsinks</c> files. Narrow on purpose, so it runs on a plain PostgreSQL.
/// </summary>
internal static partial class AuditSinksDatabase
{
    public const string Skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("auditsinks", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new TwoSources(new ChainOnly(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly)), new SinkFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>Only common 0004 (audit_log) and the audit namespace (audit_checkpoint) of the server's files.</summary>
    private sealed class ChainOnly(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common, "audit"];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
        {
            MigrationNamespaces.Common => [.. inner.Load(ns).Where(f => f.Version == 4)],
            "audit" => inner.Load(ns),
            _ => [],
        };
    }

    private sealed class TwoSources(IMigrationSource first, IMigrationSource second) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() =>
            [.. first.Namespaces().Concat(second.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. first.Load(ns).Concat(second.Load(ns))];
    }

    private sealed class SinkFiles : IMigrationSource
    {
        private static readonly Assembly _assembly = typeof(PostgresAuditSinkCursorStore).Assembly;

        public IReadOnlyList<string> Namespaces() => [.. Files().Select(f => f.Namespace).Distinct(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. Files().Where(f => f.Namespace == ns)];

        private static IEnumerable<MigrationFile> Files()
        {
            foreach (var resource in _assembly.GetManifestResourceNames())
            {
                var match = ResourcePattern().Match(resource);
                if (!match.Success || !MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name))
                {
                    continue;
                }

                using var reader = new StreamReader(_assembly.GetManifestResourceStream(resource)!);
                yield return new MigrationFile(match.Groups["namespace"].Value, version, name, reader.ReadToEnd());
            }
        }
    }

    [GeneratedRegex(@"\.Sql\.components\.(?<namespace>[^.]+)\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();
}
