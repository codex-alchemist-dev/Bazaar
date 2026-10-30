using System.Text.RegularExpressions;
using Bazaar.Server.Community.Actions;

namespace Bazaar.Server.Community.Tests.Actions;

/// <summary>Checks the SQL text and the migration file for the append-only rule. No live database is needed.</summary>
public class CommunityActionSqlTests
{
    private static readonly Regex _mutatingKeyword = new(@"\b(UPDATE|DELETE|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Insert_binds_every_column_by_name_and_never_mutates()
    {
        var sql = CommunityActionSqlText.Insert;

        foreach (var name in new[]
        {
            "agent_id", "sponsor_id", "platform", "action", "target_url", "body", "body_sha256",
            "approval_id", "taint_sources", "external_id", "status",
        })
        {
            Assert.Contains($"@{name}", sql, StringComparison.Ordinal);
        }
        Assert.Contains("RETURNING id", sql, StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO community_action", sql.TrimStart(), StringComparison.Ordinal);
        Assert.False(_mutatingKeyword.IsMatch(sql), "Insert must not contain UPDATE, DELETE or TRUNCATE.");
    }

    [Fact]
    public void List_is_parameterised_on_agent_platform_and_limit_and_never_mutates()
    {
        var sql = CommunityActionSqlText.ListForAgent;

        Assert.Contains("WHERE agent_id = @agent_id AND platform = @platform", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY created_at DESC, id DESC", sql, StringComparison.Ordinal);
        Assert.False(_mutatingKeyword.IsMatch(sql), "ListForAgent must not contain UPDATE, DELETE or TRUNCATE.");
    }

    [Fact]
    public void Migration_0003_creates_the_table_with_the_doc_36_columns_and_index()
    {
        var text = ReadMigration();

        Assert.Contains("CREATE TABLE community_action", text, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS IDENTITY PRIMARY KEY", text, StringComparison.Ordinal);
        Assert.Contains("taint_sources jsonb       NOT NULL DEFAULT '[]'", text, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX community_action_agent_platform_created", text, StringComparison.Ordinal);
        Assert.Contains("ON community_action (agent_id, platform, created_at)", text, StringComparison.Ordinal);
        Assert.Contains("TODO(encryption)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_0003_has_no_update_or_delete_statement_and_grants_insert_and_select_only()
    {
        var statements = StripComments(ReadMigration());

        Assert.False(_mutatingKeyword.IsMatch(statements), "Migration must not contain UPDATE, DELETE or TRUNCATE outside comments.");
        Assert.Contains("GRANT INSERT, SELECT ON community_action TO bazaar_app", statements, StringComparison.Ordinal);
    }

    private static string ReadMigration()
    {
        var names = typeof(CommunityActionStore).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith("0003_community_action.sql", StringComparison.Ordinal))
            .ToList();
        var name = Assert.Single(names);

        using var stream = typeof(CommunityActionStore).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Removes "--" comments so that words in prose do not count as statements.</summary>
    private static string StripComments(string sql) => Regex.Replace(sql, "--.*$", string.Empty, RegexOptions.Multiline);
}
