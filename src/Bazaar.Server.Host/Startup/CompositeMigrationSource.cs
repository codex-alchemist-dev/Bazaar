using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Host.Startup;

/// <summary>Reads migration files from several assemblies, so a module that embeds its own SQL migrates with the server's.</summary>
public sealed class CompositeMigrationSource(params IMigrationSource[] sources) : IMigrationSource
{
    /// <inheritdoc />
    public IReadOnlyList<string> Namespaces() =>
        [.. sources.SelectMany(s => s.Namespaces()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<MigrationFile> Load(string ns) => [.. sources.SelectMany(s => s.Load(ns))];
}
