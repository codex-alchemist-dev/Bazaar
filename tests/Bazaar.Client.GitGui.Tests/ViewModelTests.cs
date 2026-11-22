using Bazaar.Client.GitGui.ViewModels;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Tests;

public sealed class PrListViewModelTests
{
    [Fact]
    public async Task Pages_forward_and_back()
    {
        var client = Fixtures.ClientWith(5);
        var list = new PrListViewModel(client, "github", "acme/widgets", pageSize: 2);

        await list.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.HasMore);
        Assert.False(list.HasPrevious);

        await list.NextPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Page);
        Assert.True(list.HasPrevious);

        await list.NextPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, list.Page);
        Assert.Single(list.Items);
        Assert.False(list.HasMore);

        await list.NextPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, list.Page);

        await list.PreviousPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Page);
    }

    [Fact]
    public async Task State_filter_resets_to_first_page()
    {
        var client = new InMemoryGitGuiClient();
        client.Set(Fixtures.Detail(Fixtures.Summary(1)), [], []);
        client.Set(Fixtures.Detail(Fixtures.Summary(2, state: "merged")), [], []);
        client.Set(Fixtures.Detail(Fixtures.Summary(3)), [], []);
        var list = new PrListViewModel(client, "github", "acme/widgets", pageSize: 1);

        await list.LoadAsync(TestContext.Current.CancellationToken);
        await list.NextPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Page);

        await list.SetStateAsync("merged", TestContext.Current.CancellationToken);
        Assert.Equal(1, list.Page);
        Assert.Equal([2L], list.Items.Select(i => i.Number));

        await list.SetStateAsync("nonsense", TestContext.Current.CancellationToken);
        Assert.Equal("merged", list.State);
    }

    [Fact]
    public async Task Stale_page_shows_the_banner_with_the_provider_noun()
    {
        var client = Fixtures.ClientWith(1);
        client.Stale = true;
        var list = new PrListViewModel(client, "gitlab", "acme/widgets", 10, new FakeProviderUi("gitlab", "merge request"));
        Assert.Null(list.StaleBanner);

        await list.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(list.Stale);
        Assert.Contains("merge request", list.StaleBanner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Noun_is_neutral_without_a_provider_ui()
    {
        var list = new PrListViewModel(Fixtures.ClientWith(0), "github", "acme/widgets", 10);
        await list.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("pull request", list.PrNoun);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_page_and_reports_the_error()
    {
        var client = Fixtures.ClientWith(2);
        var list = new PrListViewModel(client, "github", "acme/widgets", 10);
        await list.LoadAsync(TestContext.Current.CancellationToken);

        client.Failure = new InvalidOperationException("server down");
        await list.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("server down", list.Error);
        Assert.False(list.IsLoading);
    }
}

public sealed class PrDetailViewModelTests
{
    private static PrDetailViewModel Create(InMemoryGitGuiClient client) => new(client, "github", "acme/widgets", 1);

    [Fact]
    public async Task Load_fills_summary_files_checks_reviews_and_card()
    {
        var detail = Create(Fixtures.ClientWith(1));
        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Body text", detail.Body);
        Assert.Equal(2, detail.Files.Count);
        Assert.Single(detail.Checks);
        Assert.Single(detail.Reviews);
        Assert.Equal(Fixtures.Head1, detail.Merge!.HeadSha);
        Assert.True(detail.Merge.CanRequestMerge);
    }

    [Fact]
    public async Task Selecting_a_file_builds_its_diff()
    {
        var detail = Create(Fixtures.ClientWith(1));
        await detail.LoadAsync(TestContext.Current.CancellationToken);

        detail.SelectFile("src/a.cs");

        Assert.NotNull(detail.SelectedDiff);
        Assert.Equal(2, detail.SelectedDiff!.Additions);
    }

    [Fact]
    public async Task Head_moved_on_refresh_clears_the_selection_and_marks_it_stale()
    {
        var client = Fixtures.ClientWith(1);
        var detail = Create(client);
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        detail.SelectFile("src/a.cs");

        client.MoveHead("github", "acme/widgets", 1, Fixtures.Head2);
        client.Replace("github", "acme/widgets", 1, Fixtures.Files, [Fixtures.Check("success", head: Fixtures.Head2)]);
        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(detail.SelectedFile);
        Assert.Null(detail.SelectedDiff);
        Assert.True(detail.SelectionStale);
        Assert.Equal("src/a.cs", detail.StaleSelectionPath);
        Assert.Equal(Fixtures.Head2, detail.Merge!.HeadSha);
        Assert.NotNull(detail.StaleNotice);

        detail.SelectFile("src/b.cs");
        Assert.False(detail.SelectionStale);
        Assert.Null(detail.StaleNotice);
    }

    [Fact]
    public async Task Head_moved_between_the_two_reads_is_retried_at_the_new_head()
    {
        var client = new MovingHeadClient(Fixtures.ClientWith(1));
        var detail = new PrDetailViewModel(client, "github", "acme/widgets", 1);

        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(detail.Error);
        Assert.Equal(Fixtures.Head2, detail.Merge!.HeadSha);
        Assert.Equal(2, client.FileCalls);
    }

    [Fact]
    public async Task Same_head_refresh_keeps_the_selection()
    {
        var detail = Create(Fixtures.ClientWith(1));
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        detail.SelectFile("src/a.cs");

        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("src/a.cs", detail.SelectedFile!.Path);
        Assert.False(detail.SelectionStale);
    }

    [Fact]
    public async Task Merge_request_raises_the_event_with_the_card_head()
    {
        var detail = Create(Fixtures.ClientWith(1));
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        OwnerMergeRequestedEventArgs? seen = null;
        detail.OwnerMergeRequested += (_, e) => seen = e;

        Assert.True(detail.RequestMerge());

        Assert.NotNull(seen);
        Assert.Equal(("github", "acme/widgets", 1L, Fixtures.Head1), (seen.Provider, seen.Repo, seen.Number, seen.HeadSha));
    }

    [Fact]
    public async Task Merge_request_on_a_blocked_card_raises_nothing()
    {
        var client = new InMemoryGitGuiClient();
        client.Set(Fixtures.Detail(Fixtures.Summary(1, draft: true)), Fixtures.Files, []);
        var detail = Create(client);
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        var raised = false;
        detail.OwnerMergeRequested += (_, _) => raised = true;

        Assert.False(detail.RequestMerge());
        Assert.False(raised);
    }

    [Fact]
    public async Task Load_failure_sets_the_error()
    {
        var client = Fixtures.ClientWith(1);
        client.Failure = new InvalidOperationException("boom");
        var detail = Create(client);

        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("boom", detail.Error);
    }

    /// <summary>Answers the first file read with head_moved after moving the head, as a push between the two reads would.</summary>
    private sealed class MovingHeadClient(InMemoryGitGuiClient inner) : IGitGuiClient
    {
        private bool _moved;

        public int FileCalls { get; private set; }

        public Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default) =>
            inner.ListPrsAsync(provider, repo, state, page, pageSize, cancellationToken);

        public Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) =>
            inner.GetPrAsync(provider, repo, number, cancellationToken);

        public Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default)
        {
            FileCalls++;
            if (!_moved)
            {
                _moved = true;
                inner.MoveHead(provider, repo, number, Fixtures.Head2);
                inner.Replace(provider, repo, number, Fixtures.Files, [Fixtures.Check("success", head: Fixtures.Head2)]);
            }

            return inner.GetFilesAsync(provider, repo, number, headSha, cancellationToken);
        }

        public Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) =>
            inner.GetChecksAsync(provider, repo, number, cancellationToken);

        public Task<GitMergedDto> MergeAsync(string provider, string repo, long number, GitMergeRequestDto request, CancellationToken cancellationToken = default) =>
            inner.MergeAsync(provider, repo, number, request, cancellationToken);
    }
}

public sealed class MergeCardTests
{
    public static TheoryData<string, bool, string, bool> Matrix => new()
    {
        { "open", false, "success", true },
        { "open", false, "skipped", true },
        { "open", false, "pending", false },
        { "open", false, "failure", false },
        { "open", true, "success", false },
        { "merged", false, "success", false },
        { "closed", false, "success", false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Gating_matrix(string state, bool draft, string checkState, bool expected)
    {
        var card = MergeCard.Evaluate(Fixtures.Summary(1, state, draft), [Fixtures.Check(checkState)]);

        Assert.Equal(expected, card.CanRequestMerge);
        Assert.Equal(expected, card.BlockedReason is null);
    }

    [Fact]
    public void Any_failing_check_blocks_even_when_others_pass()
    {
        var card = MergeCard.Evaluate(Fixtures.Summary(1), [Fixtures.Check("success"), Fixtures.Check("failure", "lint")]);
        Assert.False(card.CanRequestMerge);
        Assert.Contains("lint", card.BlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_on_an_older_head_blocks()
    {
        var card = MergeCard.Evaluate(Fixtures.Summary(1, head: Fixtures.Head2), [Fixtures.Check("success", head: Fixtures.Head1)]);
        Assert.False(card.CanRequestMerge);
    }

    [Fact]
    public void No_checks_does_not_block()
    {
        Assert.True(MergeCard.Evaluate(Fixtures.Summary(1), []).CanRequestMerge);
    }

    [Fact]
    public void Card_carries_the_head_it_was_built_for()
    {
        var card = MergeCard.Evaluate(Fixtures.Summary(7, head: Fixtures.Head2), []);
        Assert.Equal((Fixtures.Head2, 7L), (card.HeadSha, card.Number));
    }
}

public sealed class GitViewViewModelTests
{
    [Fact]
    public async Task Load_selects_the_first_repo_and_its_default_branch()
    {
        var view = new GitViewViewModel(new FakeRepoSource());
        await view.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(view.Repos);
        Assert.Equal("main", view.SelectedBranch);
        Assert.Equal(2, view.Branches.Count);
        Assert.Single(view.Commits);
    }

    [Fact]
    public async Task Constellation_link_goes_out_as_a_command_request()
    {
        var view = new GitViewViewModel(new FakeRepoSource());
        GitCommandRequest? seen = null;
        view.CommandRequested += (_, r) => seen = r;

        view.OpenInConstellation("feature");

        Assert.Equal(new GitCommandRequest("git.open-in-constellation", "feature"), seen);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Open_commit_selects_a_loaded_commit_by_sha_or_prefix()
    {
        var view = new GitViewViewModel(new FakeRepoSource());
        await view.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(view.OpenCommit(Fixtures.Head1[..8]));
        Assert.Equal(Fixtures.Head1, view.SelectedCommitSha);
        Assert.False(view.OpenCommit("deadbeefdeadbeef"));
        Assert.Null(view.SelectedCommitSha);
    }
}
