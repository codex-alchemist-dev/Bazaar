using Bazaar.Server.ReadAudit;

namespace Bazaar.Server.ReadAudit.Tests;

public class QueryShapeTests
{
    [Fact]
    public void Calls_that_differ_only_in_literal_values_hash_the_same()
    {
        var a = QueryShape.Hash("select body from gc_message where gc_id = 'gc-7' and seq > 10");
        var b = QueryShape.Hash("select body from gc_message where gc_id = 'other' and seq > 99999");

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_positional_parameter_and_a_literal_have_the_same_shape()
    {
        Assert.Equal(
            QueryShape.Hash("select id from agent where name = $1"),
            QueryShape.Hash("select id from agent where name = 'charles'"));
    }

    [Fact]
    public void Whitespace_case_and_comments_do_not_change_the_shape()
    {
        var plain = QueryShape.Hash("select id from agent where id = $1");
        var noisy = QueryShape.Hash("SELECT   id\n  FROM agent /* lookup */\n WHERE id = $1 -- by key");

        Assert.Equal(plain, noisy);
    }

    [Fact]
    public void Escaped_quotes_inside_a_literal_are_one_literal()
    {
        Assert.Equal(
            QueryShape.Hash("select 1 from gc where title = 'x'"),
            QueryShape.Hash("select 1 from gc where title = 'it''s'"));
    }

    [Fact]
    public void Different_tables_have_different_shapes()
    {
        Assert.NotEqual(
            QueryShape.Hash("select id from gc_message where seq = $1"),
            QueryShape.Hash("select id from gc_event where seq = $1"));
    }

    [Fact]
    public void A_digit_inside_an_identifier_is_part_of_the_name()
    {
        Assert.NotEqual(
            QueryShape.Hash("select 1 from gc_member2"),
            QueryShape.Hash("select 1 from gc_member3"));
    }

    [Fact]
    public void Shape_is_lowercase_hex_sha256()
    {
        var hash = QueryShape.Hash("select 1");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Theory]
    [InlineData("select id from gc", "select")]
    [InlineData("  SELECT id from gc", "select")]
    [InlineData("/* lookup */ select 1", "select")]
    [InlineData("insert into gc values ($1)", "insert")]
    [InlineData("update gc set title = $1", "update")]
    [InlineData("delete from gc where id = $1", "delete")]
    [InlineData("values (1), (2)", "values")]
    [InlineData("with recent as (select id from gc) select * from recent", "select")]
    [InlineData("with moved as (delete from gc returning id) select * from moved", "with_write")]
    [InlineData("pragma foo", "other")]
    [InlineData("", "other")]
    public void Kind_is_the_leading_statement_word(string sql, string expected)
    {
        Assert.Equal(expected, QueryShape.KindOf(sql));
    }
}
