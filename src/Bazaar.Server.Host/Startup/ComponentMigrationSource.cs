using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Reads migration files a module embeds under <c>Sql/components/{id}/NNNN_name.sql</c> (the component folder layout that the
/// Engines and Vms modules use). The namespace is the component folder name. <see cref="EmbeddedMigrationSource"/> reads only the
/// one-level <c>Sql/{namespace}/</c> layout, so it would skip these files without an error.
/// </summary>
public sealed partial class ComponentMigrationSource(Assembly assembly) : IMigrationSource
{
    /// <inheritdoc />
    public IReadOnlyList<string> Namespaces() =>
        [.. EmbeddedFiles().Select(f => f.Namespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<MigrationFile> Load(string ns) => [.. EmbeddedFiles().Where(f => f.Namespace == ns).Select(f => f.File)];

    private IEnumerable<(string Namespace, MigrationFile File)> EmbeddedFiles()
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourcePattern().Match(resource);
            if (!match.Success)
                continue;
            var ns = match.Groups["namespace"].Value;
            var fileName = match.Groups["file"].Value;
            if (!MigrationFile.TryParseFileName(fileName, out var version, out var name))
                throw new InvalidOperationException($"Migration resource '{resource}' does not follow NNNN_name.sql.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (ns, new MigrationFile(ns, version, name, reader.ReadToEnd()));
        }
    }

    [GeneratedRegex(@"\.Sql\.components\.(?<namespace>[^.]+)\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();
}
