using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.MemberRoster.Tests;

public sealed class AdminMemberRosterTests
{
    private sealed class FakeSource(int total) : IMemberRowSource
    {
        public string? Kind { get; private set; }

        public int Limit { get; private set; }

        public int Offset { get; private set; }

        public Task<MemberRawPage> FetchAsync(string? kind, int limit, int offset, CancellationToken ct)
        {
            (Kind, Limit, Offset) = (kind, limit, offset);
            var rows = Enumerable.Range(offset, Math.Max(0, Math.Min(limit, total - offset)))
                .Select(i => new MemberRawRow(Guid.NewGuid(), "m" + i, "agent", "agent", "active", null, "claude-code/haiku", 10 + i))
                .ToList();
            return Task.FromResult(new MemberRawPage(rows, total));
        }
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(9999, 200, 200)]
    [InlineData(25, 25, 25)]
    public async Task Limit_is_clamped_before_it_reaches_the_source(int asked, int expected, int _)
    {
        var src = new FakeSource(1000);
        await new AdminMemberRoster(src).ListAsync(null, asked, 0, TestContext.Current.CancellationToken);
        Assert.Equal(expected, src.Limit);
    }

    [Fact]
    public async Task Negative_offset_becomes_zero_and_kind_passes_through()
    {
        var src = new FakeSource(5);
        await new AdminMemberRoster(src).ListAsync("agent", 10, -4, TestContext.Current.CancellationToken);
        Assert.Equal((0, "agent"), (src.Offset, src.Kind));
    }

    [Fact]
    public async Task Pages_walk_the_whole_list_once_and_the_last_page_has_no_next()
    {
        var roster = new AdminMemberRoster(new FakeSource(7));
        var seen = new List<string>();
        int? offset = 0;
        var guard = 0;
        while (offset is { } o && guard++ < 10)
        {
            var page = await roster.ListAsync(null, 3, o, TestContext.Current.CancellationToken);
            Assert.Equal(7, page.Total);
            seen.AddRange(page.Rows.Select(r => r.Name));
            offset = page.NextOffset;
        }

        Assert.Equal(Enumerable.Range(0, 7).Select(i => "m" + i), seen);
    }

    [Fact]
    public async Task Offset_past_the_end_returns_no_rows_and_no_next()
    {
        var page = await new AdminMemberRoster(new FakeSource(2)).ListAsync(null, 5, 50, TestContext.Current.CancellationToken);
        Assert.Empty(page.Rows);
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public void Clean_strips_markup_and_control_characters_and_caps_length()
    {
        Assert.Equal("_script_alert(1)_/script_", AdminMemberRoster.Clean("<script>alert(1)</script>"));
        Assert.Equal("a_b", AdminMemberRoster.Clean("a\nb"));
        Assert.Equal(200, AdminMemberRoster.Clean(new string('x', 500)).Length);
        Assert.Equal(string.Empty, AdminMemberRoster.Clean(null));
    }

    [Fact]
    public async Task Negative_token_counts_are_never_shown()
    {
        var src = new OneRow(new MemberRawRow(Guid.Empty, "a", "agent", "agent", "active", null, null, -5));
        var page = await new AdminMemberRoster(src).ListAsync(null, 5, 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, page.Rows[0].Tokens24h);
        Assert.Null(page.Rows[0].Model);
    }

    private sealed class OneRow(MemberRawRow row) : IMemberRowSource
    {
        public Task<MemberRawPage> FetchAsync(string? kind, int limit, int offset, CancellationToken ct) => Task.FromResult(new MemberRawPage([row], 1));
    }
}
