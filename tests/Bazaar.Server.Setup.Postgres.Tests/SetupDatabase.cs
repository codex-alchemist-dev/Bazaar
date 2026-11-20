using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// A migrated test database holding what setup touches: the settings table (common 0003), the audit chain (common 0004, which also creates <c>bazaar_app</c>) and the
/// setup component's <c>Sql/components/setup</c> files. Narrow on purpose, so it runs on a plain PostgreSQL without pgvector.
/// </summary>
internal static partial class SetupDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("setup", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new TwoSources(new AuditOnly(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly)), new SetupFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>Only common 0002 (gateway tables), 0003 (setting) and 0004 (audit_log) of the server's files.</summary>
    private sealed class AuditOnly(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common];

        public IReadOnlyList<MigrationFile> Load(string ns) =>
            ns == MigrationNamespaces.Common ? [.. inner.Load(ns).Where(f => f.Version is 2 or 3 or 4)] : [];
    }

    private sealed class TwoSources(IMigrationSource first, IMigrationSource second) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() =>
            [.. first.Namespaces().Concat(second.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. first.Load(ns).Concat(second.Load(ns))];
    }

    /// <summary>Reads the setup assembly's embedded component migrations, as the host's component source does.</summary>
    private sealed class SetupFiles : IMigrationSource
    {
        private static readonly Assembly _assembly = typeof(PostgresApplyResultStore).Assembly;

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
