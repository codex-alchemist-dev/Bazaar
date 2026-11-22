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
