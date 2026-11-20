using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.AuditReader.Tests;

public class AdminAuditReaderTests
{
    private sealed class FakeSource(int total) : IAuditRowSource
    {
        public int LastLimit;

        public Task<IReadOnlyList<AuditRawRow>> FetchAsync(long? before, int limit, CancellationToken ct)
        {
            LastLimit = limit;
            IReadOnlyList<AuditRawRow> rows = Enumerable.Range(1, total).Reverse()
                .Select(i => (long)i)
                .Where(s => before is null || s < before)
                .Take(limit)
                .Select(s => new AuditRawRow(s, DateTimeOffset.UnixEpoch, "agent 1", "approval.granted", "merge pr"))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(9999, 200)]
    public async Task Limit_is_clamped(int asked, int expectedTake)
    {
        var source = new FakeSource(500);
        var page = await new AdminAuditReader(source).ReadAsync(null, asked, TestContext.Current.CancellationToken);

        Assert.Equal(expectedTake, page.Rows.Count);
        Assert.Equal(expectedTake + 1, source.LastLimit);
    }

    [Fact]
    public async Task Pages_newest_first_with_a_cursor()
    {
        var reader = new AdminAuditReader(new FakeSource(5));
        var first = await reader.ReadAsync(null, 2, TestContext.Current.CancellationToken);
        Assert.Equal([5L, 4L], first.Rows.Select(r => r.Seq));
        Assert.Equal(4, first.NextBefore);

        var second = await reader.ReadAsync(first.NextBefore, 2, TestContext.Current.CancellationToken);
        Assert.Equal([3L, 2L], second.Rows.Select(r => r.Seq));
        var end = await reader.ReadAsync(2, 2, TestContext.Current.CancellationToken);
        Assert.Equal([1L], end.Rows.Select(r => r.Seq));
        Assert.Null(end.NextBefore);
    }

    [Fact]
    public async Task Exact_fit_has_no_next_cursor()
    {
        var page = await new AdminAuditReader(new FakeSource(2)).ReadAsync(null, 2, TestContext.Current.CancellationToken);
        Assert.Null(page.NextBefore);
    }

    [Fact]
    public async Task Summary_is_kind_and_sanitised_action_only()
    {
        var row = (await new AdminAuditReader(new FakeSource(1)).ReadAsync(null, 1, TestContext.Current.CancellationToken)).Rows[0];
        Assert.Equal("agent_1", row.Actor);
        Assert.Equal("approval.granted: merge_pr", row.Summary);
    }

    [Fact]
    public void Labels_strip_odd_characters_and_cap_length()
    {
        Assert.Equal("a_b_c", AdminAuditReader.Label("a b\"c"));
        Assert.Equal(64, AdminAuditReader.Label(new string('x', 500)).Length);
        Assert.Equal("kind", AdminAuditReader.Summarise(new AuditRawRow(1, DateTimeOffset.UnixEpoch, "a", "kind", null)));
    }

    [Fact]
    public void Component_registers_the_port_and_depends_on_audit()
    {
        Assert.Contains("audit", AuditReaderComponent.ManifestDefinition.DependsOn);
        var services = new ServiceCollection();
        services.AddSingleton<IAuditRowSource>(new FakeSource(0));
        new AuditReaderComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();
        Assert.IsType<AdminAuditReader>(sp.GetRequiredService<IAdminAuditReader>());
    }
}
