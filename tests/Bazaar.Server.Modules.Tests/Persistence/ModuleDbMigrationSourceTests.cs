using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Modules.Tests.Persistence;

/// <summary>
/// The migration source the module fixtures apply. Without the module assembly's files the <c>modules</c> namespace is missing and
/// its tables never exist, which is what failed the Postgres-backed tests in CI. No database needed.
/// </summary>
public sealed class ModuleDbMigrationSourceTests
{
    [Fact]
    public void The_fixture_source_lists_the_modules_namespace_with_both_module_files()
    {
        var source = ModuleDb.MigrationSource;

        Assert.Contains("modules", source.Namespaces());
        var files = source.Load("modules");
        Assert.Equal([1, 2], files.Select(f => f.Version).Order().ToArray());
    }

    [Fact]
    public void The_fixture_source_still_lists_the_server_core_namespace()
    {
        Assert.Contains(MigrationRunner.Common, ModuleDb.MigrationSource.Namespaces());
    }
}
