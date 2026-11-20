using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// The migration files this module embeds under <c>Sql/components/constellation/</c> (namespace <c>constellation</c>, from
/// <c>0002</c>; <c>0001</c> stays embedded in Bazaar.Server). The host reads them through its component migration source; tests
/// and tools that build their own source use this list.
/// </summary>
public static partial class ConstellationMigrations
{
    /// <summary>The migration namespace.</summary>
    public const string Namespace = "constellation";

    /// <summary>Every embedded file of the namespace, in version order.</summary>
    public static IReadOnlyList<MigrationFile> Files()
    {
        var assembly = typeof(ConstellationMigrations).Assembly;
        var files = new List<MigrationFile>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourcePattern().Match(resource);
            if (!match.Success)
                continue;
            if (!MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name))
                throw new InvalidOperationException($"Migration resource '{resource}' does not follow NNNN_name.sql.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add(new MigrationFile(Namespace, version, name, reader.ReadToEnd()));
        }

        return [.. files.OrderBy(f => f.Version)];
    }

    [GeneratedRegex(@"\.Sql\.components\.constellation\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();
}
