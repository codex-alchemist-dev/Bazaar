using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Company.Tests;

/// <summary>
/// A migrated test database holding what the company steps touch: the audit chain (common 0004, which creates <c>bazaar_app</c>), the
/// <c>human</c> table (remote 0001) and the company component's own files. Narrow on purpose, so it runs on a plain PostgreSQL.
/// </summary>
internal static partial class CompanyDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("company", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new Combined(new ServerSubset(new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly)), new CompanyFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>The server files the steps need: common 4 (the audit chain) and remote 1 (the human table).</summary>
    private sealed class ServerSubset(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common, "remote"];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
        {
            MigrationNamespaces.Common => [.. inner.Load(ns).Where(f => f.Version == 4)],
            "remote" => [.. inner.Load(ns).Where(f => f.Version == 1)],
            _ => [],
        };
    }

    private sealed class Combined(IMigrationSource first, IMigrationSource second) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() =>
            [.. first.Namespaces().Concat(second.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. first.Load(ns).Concat(second.Load(ns))];
    }

    /// <summary>Reads the company assembly's embedded component migrations, as the host's component source does.</summary>
    private sealed class CompanyFiles : IMigrationSource
    {
        private static readonly Assembly _assembly = typeof(PostgresOwnerStore).Assembly;

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
