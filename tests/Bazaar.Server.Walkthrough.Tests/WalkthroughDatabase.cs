using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Walkthrough.Tests;

/// <summary>
/// A migrated test database holding what the walkthrough touches: the audit chain (common 0004, which also creates <c>bazaar_app</c>)
/// and the walkthrough component's <c>Sql/components/walkthrough</c> files. Narrow on purpose, so it runs on a plain PostgreSQL.
/// </summary>
internal static partial class WalkthroughDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("walkthrough", MigrateAsync, ct);

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new Combined(new AuditOnly(new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly)), new WalkthroughFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    private sealed class AuditOnly(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common];

        public IReadOnlyList<MigrationFile> Load(string ns) =>
            ns == MigrationNamespaces.Common ? [.. inner.Load(ns).Where(f => f.Version == 4)] : [];
    }

    private sealed class Combined(IMigrationSource first, IMigrationSource second) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() =>
            [.. first.Namespaces().Concat(second.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. first.Load(ns).Concat(second.Load(ns))];
    }

    private sealed class WalkthroughFiles : IMigrationSource
    {
        private static readonly Assembly _assembly = typeof(PostgresWalkthroughPrefsStore).Assembly;

        public IReadOnlyList<string> Namespaces() => [.. Files().Select(f => f.Namespace).Distinct(StringComparer.Ordinal)];

        public IReadOnlyList<MigrationFile> Load(string ns) => [.. Files().Where(f => f.Namespace == ns)];

        private static IEnumerable<MigrationFile> Files()
        {
            foreach (var resource in _assembly.GetManifestResourceNames())
            {
                var match = ResourcePattern().Match(resource);
                if (!match.Success || !MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name)) continue;
                using var reader = new StreamReader(_assembly.GetManifestResourceStream(resource)!);
                yield return new MigrationFile(match.Groups["namespace"].Value, version, name, reader.ReadToEnd());
            }
        }
    }

    [GeneratedRegex(@"\.Sql\.components\.(?<namespace>[^.]+)\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();
}
