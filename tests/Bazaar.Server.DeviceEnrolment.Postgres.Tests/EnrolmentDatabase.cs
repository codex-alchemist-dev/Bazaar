using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Core;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.DeviceEnrolment.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

/// <summary>
/// A migrated test database holding what enrolment touches: common 0004 (audit_log), the server's <c>remote</c> namespace (human,
/// device, approval_key) and this module's <c>Sql/components/device_enrolment</c> files. Narrow on purpose, so it runs on a plain
/// PostgreSQL without pgvector. Database tests run only when <c>BAZAAR_TEST_PG</c> is set.
/// </summary>
internal static partial class EnrolmentDatabase
{
    public static bool Available => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public const string SkipReason = "BAZAAR_TEST_PG is not set: no PostgreSQL to run the enrolment store against.";

    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("enrol", MigrateAsync, ct);

    /// <summary>The ordering the host's manifests give: the enrolment namespace depends on remote (human, device, approval_key).</summary>
    private static readonly ComponentManifest[] _components =
    [
        new("remote", "0.1.0", ComponentKind.Feature, [], ["feature:remote"]),
        new("device-enrolment", "0.1.0", ComponentKind.Feature, ["remote"], ["feature:device-enrolment"]),
    ];

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource, components: _components).MigrateAsync(
            new TwoSources(new ServerSubset(new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly)), new ModuleFiles()),
            MigrateCommand.CurrentProvider(),
            ct);

    /// <summary>Only common 0004 and the remote namespace of the server's files.</summary>
    private sealed class ServerSubset(IMigrationSource inner) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common, "remote"];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
        {
            MigrationNamespaces.Common => [.. inner.Load(ns).Where(f => f.Version == 4)],
            "remote" => inner.Load(ns),
            _ => [],
        };
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
        private static readonly Assembly _assembly = typeof(PostgresEnrolmentStore).Assembly;

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
