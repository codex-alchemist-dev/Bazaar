using Bazaar.Server.Git.Forge;
using Bazaar.Server.Git.Reads;

namespace Bazaar.Server.Git.Tests.Reads;

public class GitReadServiceTests
{
    private readonly FakeForge _forge = new();

    private GitReadService Service(GitReadOptions? options = null) => new(new ForgeRegistry([_forge], []), options);

    private static readonly RepoRef _repo = new("fake", "acme/widgets");

    [Fact]
    public async Task List_maps_to_dto()
    {
        var page = await Service().ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(page.Items);
        Assert.False(page.Stale);
        Assert.Equal("open", item.State);
        Assert.Equal("acme/widgets", item.Repo);
        Assert.Equal(10_000, item.UpdatedAtMs);
        Assert.Equal("feat", item.HeadBranch);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(500, 100)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    public async Task Page_size_is_clamped(int? requested, int expected)
    {
        await Service().ListPrsAsync(_repo, PrState.Open, 1, requested, TestContext.Current.CancellationToken);
        Assert.Equal(expected, _forge.LastQuery!.PageSize);
    }

    [Fact]
    public async Task Configured_page_size_is_clamped_to_100()
    {
        await Service(new GitReadOptions { PrPageSize = 5000 }).ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken);
        Assert.Equal(100, _forge.LastQuery!.PageSize);
    }

    [Fact]
    public async Task Outage_serves_last_cached_page_marked_stale()
    {
        var svc = Service();
        var fresh = await svc.ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken);
        _forge.OnList = _ => throw new HttpRequestException("down");

        var stale = await svc.ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken);

        Assert.True(stale.Stale);
        Assert.Equal(fresh.Items, stale.Items);
    }

    [Fact]
    public async Task Outage_with_nothing_cached_rethrows_and_invents_nothing()
    {
        _forge.OnList = _ => throw new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => Service().ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_is_keyed_by_query()
    {
        var svc = Service();
        await svc.ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken);
        _forge.OnList = _ => throw new HttpRequestException("down");

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.ListPrsAsync(_repo, null, 2, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_is_bounded()
    {
        var svc = Service();
        for (var p = 1; p <= GitReadService.MaxCachedPages + 5; p++)
            await svc.ListPrsAsync(_repo, null, p, null, TestContext.Current.CancellationToken);
        _forge.OnList = _ => throw new HttpRequestException("down");

        // The oldest page was evicted; the newest is still there.
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.ListPrsAsync(_repo, null, 1, null, TestContext.Current.CancellationToken));
        var stale = await svc.ListPrsAsync(_repo, null, GitReadService.MaxCachedPages + 5, null, TestContext.Current.CancellationToken);
        Assert.True(stale.Stale);
    }

    [Fact]
    public async Task Unknown_provider_is_not_swallowed_into_the_stale_cache()
    {
        await Assert.ThrowsAsync<UnknownForgeProviderException>(() => Service().ListPrsAsync(new RepoRef("nope", "a/b"), null, 1, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detail_maps_reviews()
    {
        var d = await Service().GetPrAsync(TestData.Pr, TestContext.Current.CancellationToken);

        Assert.Equal(7, d.Summary.Number);
        Assert.Equal("request_changes", Assert.Single(d.Reviews).Verdict);
        Assert.Equal(3, d.Additions);
    }

    [Fact]
    public async Task Files_with_a_moved_head_throw_head_moved_with_the_current_sha()
    {
        _forge.Head = TestData.OtherSha;

        var ex = await Assert.ThrowsAsync<HeadMovedException>(() => Service().GetFilesAsync(TestData.Pr, TestData.Sha, TestContext.Current.CancellationToken));

        Assert.Equal(TestData.OtherSha, ex.CurrentSha);
    }

    [Fact]
    public async Task Files_over_the_cap_are_cut_and_flagged()
    {
        _forge.Files = [new PrFile("big.txt", "modified", 1, 0, new string('a', 100_000), false), new PrFile("small.txt", "added", 1, 0, "x", false)];

        var files = await Service(new GitReadOptions { MaxDiffBytes = 70_000 }).GetFilesAsync(TestData.Pr, TestData.Sha, TestContext.Current.CancellationToken);

        Assert.True(files[0].Truncated);
        Assert.Equal(70_000, files[0].Patch!.Length);
        Assert.False(files[1].Truncated);
        Assert.Equal("x", files[1].Patch);
    }

    [Fact]
    public async Task Cap_floor_is_64_kib()
    {
        _forge.Files = [new PrFile("f", "modified", 1, 0, new string('a', 70_000), false)];

        var files = await Service(new GitReadOptions { MaxDiffBytes = 10 }).GetFilesAsync(TestData.Pr, TestData.Sha, TestContext.Current.CancellationToken);

        Assert.Equal(65536, files[0].Patch!.Length);
        Assert.True(files[0].Truncated);
    }

    [Fact]
    public async Task Forge_truncation_flag_is_kept()
    {
        _forge.Files = [new PrFile("f", "modified", 1, 0, "short", true)];
        var files = await Service().GetFilesAsync(TestData.Pr, TestData.Sha, TestContext.Current.CancellationToken);
        Assert.True(files[0].Truncated);
    }

    [Fact]
    public void Cut_never_splits_a_multibyte_character()
    {
        // 'é' is 2 bytes; a 3-byte cap lands in the middle of the second one.
        var (patch, cut) = GitReadService.CutPatch("ééé", 3);

        Assert.True(cut);
        Assert.Equal("é", patch);
    }

    [Fact]
    public void Missing_patch_stays_missing() => Assert.Equal((null, false), GitReadService.CutPatch(null, 10));

    [Fact]
    public async Task Checks_use_the_current_head_and_lower_case_states()
    {
        _forge.Checks = [new CheckRun("ci", CheckState.Pending, TestData.Sha, "https://ci.example.test/1")];

        var checks = await Service().GetChecksAsync(TestData.Pr, TestContext.Current.CancellationToken);

        var c = Assert.Single(checks);
        Assert.Equal("pending", c.State);
        Assert.Equal("https://ci.example.test/1", c.DetailsUrl);
    }
}
