using System.Text.RegularExpressions;
using Bazaar.Server.Community.Observatory;

namespace Bazaar.Server.Community.Tests.Observatory;

/// <summary>Checks the Observatory read SQL: no body column in the select list, and no write statement.</summary>
public class CommunityLogSqlTests
{
    private static readonly Regex _mutatingKeyword = new(@"\b(UPDATE|DELETE|INSERT|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex _bodyWord = new(@"\bbody\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Select_list_has_no_body_column()
    {
        var selectList = SelectList(CommunityLogSqlText.Select);

        Assert.False(_bodyWord.IsMatch(selectList), $"Select list must not name the body column: {selectList}");
        Assert.Contains("body_sha256", selectList, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_list_names_every_row_column()
    {
        var selectList = SelectList(CommunityLogSqlText.Select);

        foreach (var column in new[]
        {
            "id", "agent_id", "platform", "action", "target_url", "body_sha256", "status", "created_at", "approval_id", "external_id",
        })
        {
            Assert.Matches(new Regex($@"\b{column}\b"), selectList);
        }
    }

    [Fact]
    public void Select_is_read_only_parameterised_and_ordered_newest_first()
    {
        var sql = CommunityLogSqlText.Select;

        Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.Ordinal);
        Assert.False(_mutatingKeyword.IsMatch(sql), "Select must not contain UPDATE, DELETE, INSERT or TRUNCATE.");
        Assert.Contains("ORDER BY created_at DESC, id DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit", sql, StringComparison.Ordinal);
        Assert.Contains("@agent_id", sql, StringComparison.Ordinal);
        Assert.Contains("@platform", sql, StringComparison.Ordinal);
    }

    private static string SelectList(string sql)
    {
        var start = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) + "SELECT".Length;
        var end = sql.IndexOf("FROM", start, StringComparison.OrdinalIgnoreCase);
        Assert.True(end > start, "Could not find the select list between SELECT and FROM.");
        return sql[start..end];
    }
}
