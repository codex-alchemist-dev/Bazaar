using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Modules.LineMove;
using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Modules.Tests.Persistence;

/// <summary>
/// The module manager migration (<c>modules/0001</c>) read as text: file naming and order, and the CHECK lists against the C#
/// enums they mirror. Pure logic, no database needed.
/// </summary>
public sealed partial class ModuleManagerMigrationTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(MigrationRunner).Assembly);

    private static string ModuleSql =>
        Assert.Single(_source.Load("modules"), f => f.Version == 1 && f.Name == "module_manager").Sql;

    /// <summary>The quoted values of the CHECK list for one column, for example <c>decision IN ('Auto', 'Manual')</c>.</summary>
    private static HashSet<string> CheckValues(string sql, string column)
    {
        var list = Assert.Single(Regex.Matches(sql, $@"\b{column} IN \(([^)]*)\)", RegexOptions.Singleline)).Groups[1].Value;
        return [.. QuotedValue().Matches(list).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void The_module_manager_file_is_the_first_file_of_its_own_namespace()
    {
        var files = _source.Load("modules");

        Assert.Equal(2, files.Count);
        var file = files[0];
        Assert.Equal("modules", file.Namespace);
        Assert.Equal(1, file.Version);
        Assert.Equal("module_manager", file.Name);
    }

    [Fact]
    public void Migration_file_names_are_unique_and_versions_are_ordered_in_every_namespace()
    {
        foreach (var ns in _source.Namespaces())
        {
            var files = _source.Load(ns);

            Assert.Equal(files.Count, files.Select(f => f.Version).Distinct().Count());
            Assert.Equal(files.Count, files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());

            var ordered = files.OrderBy(f => f.Version).Select(f => f.Version).ToList();
            Assert.Equal(ordered, ordered.Order());
            Assert.All(files, f => Assert.True(f.Version >= 1, $"{ns} has a version below 1"));
        }
    }

    [Fact]
    public void The_module_manager_namespace_is_a_valid_namespace_name()
    {
        Assert.Contains("modules", _source.Namespaces());
        Assert.True(MigrationNamespaces.IsValid("modules"));
    }

    [Fact]
    public void The_decision_check_lists_exactly_the_update_decision_names()
    {
        Assert.Equal(
            Enum.GetNames<UpdateDecision>().ToHashSet(StringComparer.Ordinal),
            CheckValues(ModuleSql, "decision"));
    }

    [Fact]
    public void The_step_check_lists_exactly_the_line_move_step_names()
    {
        Assert.Equal(
            Enum.GetNames<LineMoveStep>().ToHashSet(StringComparer.Ordinal),
            CheckValues(ModuleSql, "step"));
    }

    [Fact]
    public void The_outcome_check_lists_exactly_the_line_move_audit_kind_names()
    {
        Assert.Equal(
            Enum.GetNames<LineMoveAuditKind>().ToHashSet(StringComparer.Ordinal),
            CheckValues(ModuleSql, "outcome"));
    }

    [Fact]
    public void Both_tables_refuse_update_delete_and_truncate_by_trigger()
    {
        foreach (var table in new[] { "module_update_decision", "module_line_move_event" })
        {
            Assert.Contains($"{table}_no_update_or_delete BEFORE UPDATE OR DELETE ON {table}", ModuleSql, StringComparison.Ordinal);
            Assert.Contains($"{table}_no_truncate BEFORE TRUNCATE ON {table}", ModuleSql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_app_role_gets_insert_and_select_only()
    {
        Assert.Contains("GRANT SELECT, INSERT ON module_update_decision, module_line_move_event TO bazaar_app;", ModuleSql, StringComparison.Ordinal);
        // Only GRANT statements count here: the triggers name UPDATE and DELETE on purpose.
        // The keywords are upper case in the file; table names such as module_update_decision are not.
        var grants = Regex.Matches(ModuleSql, "GRANT[^;]*;").Select(m => m.Value).ToList();
        Assert.NotEmpty(grants);
        Assert.All(grants, g => Assert.False(Regex.IsMatch(g, @"\b(UPDATE|DELETE)\b"), g));
    }

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedValue();
}
