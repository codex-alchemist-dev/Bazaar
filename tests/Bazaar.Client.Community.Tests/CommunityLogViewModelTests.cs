using System.Text.Json;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Tests;

public class CommunityLogViewModelTests
{
    private static CommunityLogRowDto Row(long id, string agent = "agent-1", string platform = "github", string? hash = "ab12") =>
        new(id, agent, platform, "Post", "owner/repo#1", hash, "done", Fakes.Now.AddMinutes(-id), null, null);

    private sealed class Source : ICommunityLogSource
    {
        public List<CommunityLogQueryDto> Queries { get; } = [];

        public Func<CommunityLogQueryDto, CommunityLogPageDto> Respond { get; set; } = _ => new CommunityLogPageDto([], null);

        public Task<CommunityLogPageDto> LoadAsync(CommunityLogQueryDto query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(Respond(query));
        }
    }

    [Fact]
    public async Task Refresh_loads_rows_in_server_order_and_reports_status()
    {
        var source = new Source { Respond = _ => new CommunityLogPageDto([Row(1), Row(2)], "c1") };
        var vm = new CommunityLogViewModel(source);

        await vm.RefreshAsync();

        Assert.Equal([1L, 2L], vm.Rows.Select(r => r.Id));
        Assert.True(vm.CanLoadMore);
        Assert.False(vm.IsEmpty);
        Assert.Contains("More are available", vm.StatusText);
        Assert.Null(source.Queries[0].Cursor);
    }

    [Fact]
    public async Task Load_more_sends_the_cursor_and_appends()
    {
        var source = new Source
        {
            Respond = q => q.Cursor is null
                ? new CommunityLogPageDto([Row(1)], "c1")
                : new CommunityLogPageDto([Row(2)], null),
        };
        var vm = new CommunityLogViewModel(source);
        await vm.RefreshAsync();

        await vm.LoadMoreAsync();

        Assert.Equal("c1", source.Queries[1].Cursor);
        Assert.Equal([1L, 2L], vm.Rows.Select(r => r.Id));
        Assert.False(vm.CanLoadMore);
        Assert.Contains("end of the log", vm.StatusText);
    }

    [Fact]
    public async Task Filters_are_trimmed_and_blank_means_all()
    {
        var source = new Source();
        var vm = new CommunityLogViewModel(source, pageSize: 20) { AgentFilter = " agent-2 ", PlatformFilter = "  " };

        await vm.RefreshAsync();

        var query = Assert.Single(source.Queries);
        Assert.Equal("agent-2", query.AgentId);
        Assert.Null(query.Platform);
        Assert.Equal(20, query.Limit);
    }

    [Fact]
    public async Task Refresh_replaces_rows_and_drops_the_old_cursor()
    {
        var source = new Source { Respond = _ => new CommunityLogPageDto([Row(1)], "c1") };
        var vm = new CommunityLogViewModel(source);
        await vm.RefreshAsync();
        source.Respond = _ => new CommunityLogPageDto([Row(9)], null);

        await vm.RefreshAsync();

        Assert.Equal([9L], vm.Rows.Select(r => r.Id));
        Assert.False(vm.CanLoadMore);
    }

    [Fact]
    public async Task Empty_result_is_an_empty_state_not_an_error()
    {
        var vm = new CommunityLogViewModel(new Source());

        await vm.RefreshAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Source_failure_shows_an_error_and_clears_rows()
    {
        var source = new Source { Respond = _ => new CommunityLogPageDto([Row(1)], null) };
        var vm = new CommunityLogViewModel(source);
        await vm.RefreshAsync();
        source.Respond = _ => throw new InvalidOperationException("server down");

        await vm.RefreshAsync();

        Assert.True(vm.HasError);
        Assert.Equal("server down", vm.Error);
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsEmpty);
        Assert.Contains("could not be loaded", vm.StatusText);
    }

    [Fact]
    public async Task Failed_load_more_keeps_the_rows_already_shown()
    {
        var source = new Source { Respond = _ => new CommunityLogPageDto([Row(1)], "c1") };
        var vm = new CommunityLogViewModel(source);
        await vm.RefreshAsync();
        source.Respond = _ => throw new InvalidOperationException("boom");

        await vm.LoadMoreAsync();

        Assert.True(vm.HasError);
        Assert.Single(vm.Rows);
        Assert.True(vm.CanLoadMore);
    }

    [Fact]
    public void Row_shows_status_and_hash_in_words_and_has_no_body()
    {
        var row = new CommunityLogRowViewModel(Row(1, hash: null));

        Assert.Equal("no body", row.BodyHash);
        Assert.Equal("done", row.Status);
        Assert.Equal("none", row.Approval);
        Assert.Contains("agent-1 Post on github, done", row.Summary);
        Assert.DoesNotContain(typeof(CommunityLogRowViewModel).GetProperties(), p => p.Name.Contains("Body") && p.Name != "BodyHash");
        Assert.DoesNotContain(typeof(CommunityLogRowDto).GetProperties(), p => p.Name == "Body");
    }

    [Fact]
    public void Contracts_round_trip_through_the_source_generated_context()
    {
        var page = new CommunityLogPageDto([Row(1)], "c1");

        var json = JsonSerializer.Serialize(page, CommunityContractsJsonContext.Default.CommunityLogPageDto);
        var back = JsonSerializer.Deserialize(json, CommunityContractsJsonContext.Default.CommunityLogPageDto);

        Assert.Equal("c1", back!.NextCursor);
        Assert.Equal(1L, Assert.Single(back.Rows).Id);
        Assert.DoesNotContain("\"body\"", json);
    }
}
