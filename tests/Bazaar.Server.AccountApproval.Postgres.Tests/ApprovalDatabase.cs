using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.AccountApproval.Postgres;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.AccountApproval.Postgres.Tests;

/// <summary>
/// A migrated test database holding what account approval touches: common 0004 (which creates the <c>bazaar_app</c> role the grants name)
/// and the module's <c>Sql/components/account_approval</c> files. Narrow on purpose, so it runs on a plain PostgreSQL.
/// </summary>
internal static partial class ApprovalDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("approval", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new TwoSources(new RoleOnly(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly)), new ModuleFiles()),
            new TwoSources(new RoleOnly(new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly)), new ModuleFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>Only common 0004 of the server's files.</summary>
    private sealed class RoleOnly(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common];

        public IReadOnlyList<MigrationFile> Load(string ns) =>
            ns == MigrationNamespaces.Common ? [.. inner.Load(ns).Where(f => f.Version == 4)] : [];
    }

    private sealed class TwoSources(IMigrationSource first, IMigrationSource second) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() =>
            [.. first.Namespaces().Concat(second.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. first.Load(ns).Concat(second.Load(ns))];
    }

    /// <summary>Reads the module assembly's embedded component migrations, as the host's component source does.</summary>
    private sealed class ModuleFiles : IMigrationSource
    {
        private static readonly Assembly _assembly = typeof(PostgresAccountApprovalStore).Assembly;

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
