using System.Reflection;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Data.Migrations;

/// <summary>
/// Where migration files come from. A namespace is one folder under <c>Data/Sql</c>: <c>common</c>, a host provider folder
/// (<c>windows</c>, <c>linux</c>) or a Bazaar Component's id-derived folder (section 5 of the components design).
/// </summary>
public interface IMigrationSource
{
    /// <summary>Names of the namespace folders that hold at least one file, in any order.</summary>
    IReadOnlyList<string> Namespaces();

    /// <summary>Files of one namespace, in any order. Empty when the namespace does not exist.</summary>
    IReadOnlyList<MigrationFile> Load(string ns);
}

/// <summary>Reads <c>Data/Sql/{namespace}/NNNN_name.sql</c> files embedded in an assembly.</summary>
public sealed partial class EmbeddedMigrationSource(Assembly assembly) : IMigrationSource
{
    public IReadOnlyList<string> Namespaces() =>
        [.. EmbeddedFiles().Select(f => f.Namespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<MigrationFile> Load(string ns) => [.. EmbeddedFiles().Where(f => f.Namespace == ns).Select(f => f.File)];

    /// <summary>
    /// Every <c>.sql</c> resource under a <c>Sql</c> folder. MSBuild writes folder names into resource names with <c>-</c> replaced by
    /// <c>_</c>, which is why namespace names are snake_case (see <see cref="MigrationNamespaces.IsValid"/>).
    /// </summary>
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

    [GeneratedRegex(@"\.Sql\.(?<namespace>[^.]+)\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();
}

/// <summary>Fixed list of files; used by tests and tools.</summary>
public sealed class InMemoryMigrationSource(IEnumerable<MigrationFile> files) : IMigrationSource
{
    private readonly List<MigrationFile> _files = [.. files];

    public IReadOnlyList<string> Namespaces() => [.. _files.Select(f => f.Namespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<MigrationFile> Load(string ns) => [.. _files.Where(f => f.Namespace == ns)];
}
