using System.Reflection;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Data.Migrations;

/// <summary>Where migration files come from. Providers are the folder names (<c>common</c>, <c>windows</c>, <c>linux</c>).</summary>
public interface IMigrationSource
{
    /// <summary>Files of one provider folder, in any order. Empty when the folder does not exist.</summary>
    IReadOnlyList<MigrationFile> Load(string provider);
}

/// <summary>Reads <c>Data/Sql/{provider}/NNNN_name.sql</c> files embedded in an assembly.</summary>
public sealed partial class EmbeddedMigrationSource(Assembly assembly) : IMigrationSource
{
    public IReadOnlyList<MigrationFile> Load(string provider)
    {
        var files = new List<MigrationFile>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourcePattern().Match(resource);
            if (!match.Success || match.Groups["provider"].Value != provider)
                continue;
            if (!MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name))
                throw new InvalidOperationException($"Migration resource '{resource}' does not follow NNNN_name.sql.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add(new MigrationFile(provider, version, name, reader.ReadToEnd()));
        }

        return files;
    }

    [GeneratedRegex(@"\.Sql\.(?<provider>[a-z]+)\.(?<file>[A-Za-z0-9_]+\.sql)$")]
    private static partial Regex ResourcePattern();
}

/// <summary>Fixed list of files; used by tests and tools.</summary>
public sealed class InMemoryMigrationSource(IEnumerable<MigrationFile> files) : IMigrationSource
{
    private readonly List<MigrationFile> _files = [.. files];

    public IReadOnlyList<MigrationFile> Load(string provider) => [.. _files.Where(f => f.Provider == provider)];
}
