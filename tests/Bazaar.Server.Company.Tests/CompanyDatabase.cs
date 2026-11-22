using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Company.Tests;

/// <summary>
/// A migrated test database holding what the company seed touches: the audit chain (common 0004, which creates <c>bazaar_app</c>),
/// the agent tables (common 0010), the human tables (remote 0001), the mention handles (mentions 0001) and the company component's
/// own files. Narrow on purpose, so it runs on a plain PostgreSQL.
/// </summary>
internal static partial class CompanyDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("company", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new Combined(new ServerSubset(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly)), new CompanyFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>The server files the seed needs: common 4 and 10, remote 1 and mentions 1.</summary>
    private sealed class ServerSubset(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common, "remote", "mentions"];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
        {
            MigrationNamespaces.Common => [.. inner.Load(ns).Where(f => f.Version is 4 or 10)],
            "remote" => [.. inner.Load(ns).Where(f => f.Version == 1)],
            "mentions" => [.. inner.Load(ns).Where(f => f.Version == 1)],
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
        private static readonly Assembly _assembly = typeof(PostgresCompanySeeder).Assembly;

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
