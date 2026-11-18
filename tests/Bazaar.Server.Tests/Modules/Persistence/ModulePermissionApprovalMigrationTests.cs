using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Tests.Modules.Persistence;

/// <summary>The text of <c>modules/0002</c>: the table's guards and grants. The behaviour is checked against Postgres in <see cref="Install.ModuleApprovalPersistenceTests"/>.</summary>
public sealed class ModulePermissionApprovalMigrationTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(Bazaar.Server.AssemblyMarker).Assembly);

    private static string Sql => Assert.Single(_source.Load("modules"), f => f.Version == 2 && f.Name == "permission_approval").Sql;

    [Fact]
    public void The_approval_file_is_the_second_file_of_the_modules_namespace()
    {
        var files = _source.Load("modules");

        Assert.Equal(2, files.Count);
        Assert.Equal(2, files[1].Version);
        Assert.Equal("permission_approval", files[1].Name);
    }

    [Fact]
    public void The_approval_table_refuses_update_delete_and_truncate_by_trigger()
    {
        var sql = Sql;

        Assert.Matches(new Regex(@"CREATE TRIGGER module_permission_approval_no_update_or_delete BEFORE UPDATE OR DELETE ON module_permission_approval\s+FOR EACH ROW EXECUTE FUNCTION module_manager_refuse_change"), sql);
        Assert.Matches(new Regex(@"CREATE TRIGGER module_permission_approval_no_truncate BEFORE TRUNCATE ON module_permission_approval\s+FOR EACH STATEMENT EXECUTE FUNCTION module_manager_refuse_change"), sql);
    }

    [Fact]
    public void The_app_role_gets_insert_and_select_only()
    {
        var sql = Sql;

        Assert.Contains("GRANT SELECT, INSERT ON module_permission_approval TO bazaar_app;", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT USAGE ON SEQUENCE module_permission_approval_id_seq TO bazaar_app;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ON module_permission_approval", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_table_refuses_an_empty_permission_set_an_unsigned_row_and_a_malformed_digest()
    {
        var sql = Sql;

        Assert.Contains("CHECK (cardinality(permissions) > 0)", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (octet_length(signature) > 0)", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (approval_digest_sha256 ~ '^[0-9a-f]{64}$')", sql, StringComparison.Ordinal);
    }
}
