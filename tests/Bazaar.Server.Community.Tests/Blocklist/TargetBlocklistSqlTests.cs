using System.Text.RegularExpressions;
using Bazaar.Server.Community.Blocklist;

namespace Bazaar.Server.Community.Tests.Blocklist;

/// <summary>Checks the blocklist SQL text and migration 0004 for the append-only rule. No live database is needed.</summary>
public class TargetBlocklistSqlTests
{
    private static readonly Regex _mutatingKeyword = new(@"\b(UPDATE|DELETE|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Insert_uses_on_conflict_do_nothing_and_binds_every_column()
    {
        var sql = TargetBlocklistSqlText.Insert;

        Assert.Contains("ON CONFLICT (platform, target_ref) DO NOTHING", sql, StringComparison.Ordinal);
        foreach (var name in new[] { "platform", "target_ref", "reason", "added_by" })
        {
            Assert.Contains($"@{name}", sql, StringComparison.Ordinal);
        }
        Assert.StartsWith("INSERT INTO target_blocklist", sql.TrimStart(), StringComparison.Ordinal);
        Assert.False(_mutatingKeyword.IsMatch(sql), "Insert must not contain UPDATE, DELETE or TRUNCATE.");
    }

    [Fact]
    public void IsBlocked_is_parameterised_and_never_mutates()
    {
        var sql = TargetBlocklistSqlText.IsBlocked;

        Assert.Contains("WHERE platform = @platform AND target_ref = @target_ref", sql, StringComparison.Ordinal);
        Assert.False(_mutatingKeyword.IsMatch(sql), "IsBlocked must not contain UPDATE, DELETE or TRUNCATE.");
    }

    [Fact]
    public void Migration_0004_creates_the_table_with_the_primary_key()
    {
        var text = ReadMigration();

        Assert.Contains("CREATE TABLE target_blocklist", text, StringComparison.Ordinal);
        Assert.Contains("platform   text        NOT NULL", text, StringComparison.Ordinal);
        Assert.Contains("target_ref text        NOT NULL", text, StringComparison.Ordinal);
        Assert.Contains("reason     text        NOT NULL", text, StringComparison.Ordinal);
        Assert.Contains("added_by   text        NOT NULL", text, StringComparison.Ordinal);
        Assert.Contains("added_at   timestamptz NOT NULL DEFAULT now()", text, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (platform, target_ref)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_0004_has_no_update_or_delete_statement_and_grants_insert_and_select_only()
    {
        var statements = StripComments(ReadMigration());

        Assert.False(_mutatingKeyword.IsMatch(statements), "Migration must not contain UPDATE, DELETE or TRUNCATE outside comments.");
        Assert.Contains("GRANT INSERT, SELECT ON target_blocklist TO bazaar_app", statements, StringComparison.Ordinal);
    }

    private static string ReadMigration()
    {
        var names = typeof(TargetBlocklistSqlText).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith("0004_target_blocklist.sql", StringComparison.Ordinal))
            .ToList();
        var name = Assert.Single(names);

        using var stream = typeof(TargetBlocklistSqlText).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string StripComments(string sql) =>
        string.Join('\n', sql.Split('\n').Select(line =>
        {
            var at = line.IndexOf("--", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }));
}
