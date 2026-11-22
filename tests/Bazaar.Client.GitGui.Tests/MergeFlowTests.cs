using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.GitGui.Panes;
using Bazaar.Client.GitGui.ViewModels;
using Bazaar.Contracts.Git;

#pragma warning disable xUnit1051

namespace Bazaar.Client.GitGui.Tests;

internal sealed class FakeApprover(MergeApprovalOutcome outcome = MergeApprovalOutcome.Approved) : IOwnerMergeApprover
{
    public List<MergeApprovalRequest> Requests { get; } = [];

    public Task<MergeApprovalResult> ApproveAsync(MergeApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (outcome != MergeApprovalOutcome.Approved)
        {
            return Task.FromResult(new MergeApprovalResult(outcome, null));
        }

        var approval = new GitOwnerApprovalDto("pr.merge", Guid.NewGuid(), "AA==", request.HeadSha, Guid.Empty, "dev", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "AA==", "AA==");
        return Task.FromResult(new MergeApprovalResult(outcome, new GitMergeRequestDto(request.HeadSha, request.CommitTitle, "Signed-off-by: t", approval)));
    }
}

public sealed class MergeFlowTests
{
    private static async Task<(PrDetailViewModel Detail, InMemoryGitGuiClient Client)> LoadedAsync(IOwnerMergeApprover? approver, IGitGuiClient? wrap = null)
    {
        var client = Fixtures.ClientWith(1);
        var detail = new PrDetailViewModel(wrap ?? client, "github", "acme/widgets", 1, mergeApprover: approver);
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        return (detail, client);
    }

    [Fact]
    public async Task Merge_signs_the_card_head_posts_it_and_reloads()
    {
        var approver = new FakeApprover();
        var (detail, client) = await LoadedAsync(approver);

        Assert.True(await detail.MergeAsync(TestContext.Current.CancellationToken));

        var asked = Assert.Single(approver.Requests);
        Assert.Equal(("github", "acme/widgets", 1L, Fixtures.Head1, "PR 1 (#1)"), (asked.Provider, asked.Repo, asked.Number, asked.HeadSha, asked.CommitTitle));
        Assert.Equal(Fixtures.Head1, Assert.Single(client.MergeRequests).HeadSha);
        Assert.Equal(MergeCardState.Merged, detail.MergeState);
        Assert.False(detail.CanMerge);
        Assert.Equal("merged", detail.Detail!.Summary.State);
    }

    [Fact]
    public async Task A_blocked_card_never_asks_for_approval()
    {
        var approver = new FakeApprover();
        var client = new InMemoryGitGuiClient();
        client.Set(Fixtures.Detail(Fixtures.Summary(1, draft: true)), Fixtures.Files, []);
        var detail = new PrDetailViewModel(client, "github", "acme/widgets", 1, mergeApprover: approver);
        await detail.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));
        Assert.Empty(approver.Requests);
        Assert.Empty(client.MergeRequests);
    }

    [Fact]
    public async Task Without_an_approver_the_card_says_it_cannot_approve()
    {
        var (detail, client) = await LoadedAsync(null);

        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MergeCardState.CannotApprove, detail.MergeState);
        Assert.Empty(client.MergeRequests);
    }

    [Theory]
    [InlineData(MergeApprovalOutcome.Cancelled, MergeCardState.Cancelled)]
    [InlineData(MergeApprovalOutcome.Unavailable, MergeCardState.CannotApprove)]
    [InlineData(MergeApprovalOutcome.Failed, MergeCardState.Failed)]
    public async Task A_refused_approval_sends_nothing(MergeApprovalOutcome outcome, MergeCardState expected)
    {
        var (detail, client) = await LoadedAsync(new FakeApprover(outcome));

        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, detail.MergeState);
        Assert.Empty(client.MergeRequests);
        Assert.True(detail.CanMerge);
    }

    [Fact]
    public async Task Head_moved_reloads_and_says_to_review_again()
    {
        var (detail, client) = await LoadedAsync(new FakeApprover());
        client.MoveHead("github", "acme/widgets", 1, Fixtures.Head2);
        client.Replace("github", "acme/widgets", 1, Fixtures.Files, [Fixtures.Check("success", head: Fixtures.Head2)]);

        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MergeCardState.HeadMoved, detail.MergeState);
        Assert.Contains("head moved", detail.MergeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Fixtures.Head2, detail.Merge!.HeadSha);
        Assert.True(detail.CanMerge);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Server_refusals_map_to_card_states(Exception failure, MergeCardState expected, string text)
    {
        var (detail, client) = await LoadedAsync(new FakeApprover());
        client.MergeFailure = failure;

        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, detail.MergeState);
        Assert.Contains(text, detail.MergeMessage, StringComparison.Ordinal);
        Assert.True(detail.CanMerge);
    }

    public static TheoryData<Exception, MergeCardState, string> Refusals => new()
    {
        { new GitMergeNotApprovedException(), MergeCardState.NotApproved, "Not approved" },
        { new GitRateLimitedException(30), MergeCardState.RateLimited, "30 seconds" },
        { new GitRateLimitedException(null), MergeCardState.RateLimited, "later" },
        { new GitServerUnavailableException("secret forge text"), MergeCardState.Unavailable, "unavailable" },
        { new GitRequestException(400, "bad_request"), MergeCardState.Failed, "could not be completed" },
    };

    [Fact]
    public async Task A_second_click_while_merging_is_ignored()
    {
        var gate = new TaskCompletionSource<GitMergedDto>();
        var slow = new SlowClient(Fixtures.ClientWith(1), gate.Task);
        var (detail, _) = await LoadedAsync(new FakeApprover(), slow);

        var first = detail.MergeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MergeCardState.Merging, detail.MergeState);
        Assert.False(await detail.MergeAsync(TestContext.Current.CancellationToken));
        gate.SetResult(new GitMergedDto(Fixtures.Head1));
        Assert.True(await first);
        Assert.Equal(1, slow.Calls);
    }

    [Fact]
    public async Task The_message_never_carries_server_text_or_the_signature()
    {
        var (detail, client) = await LoadedAsync(new FakeApprover());
        client.MergeFailure = new GitServerUnavailableException("https://forge.example/secret?token=abc");

        await detail.MergeAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret", detail.MergeMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Card_shows_the_head_moved_notice_and_re_enables_the_button()
    {
        var client = Fixtures.ClientWith(1);
        var registry = Fixtures.Registry(client, new FakeApprover(), null);
        var pane = (GitPrPaneViewModel)registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);
        var view = registry.CreateView(pane);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Request owner merge"));
        Assert.True(button.IsEnabled);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        client.MoveHead("github", "acme/widgets", 1, Fixtures.Head2);
        client.Replace("github", "acme/widgets", 1, Fixtures.Files, [Fixtures.Check("success", head: Fixtures.Head2)]);
        await pane.Detail.MergeAsync(TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("The head moved. Review the new head and approve again.", texts);
        Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Request owner merge")).IsEnabled);
    }

    [AvaloniaFact]
    public async Task Card_disables_the_button_once_merged()
    {
        var registry = Fixtures.Registry(Fixtures.ClientWith(1), new FakeApprover(), null);
        var pane = (GitPrPaneViewModel)registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);
        var view = registry.CreateView(pane);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await pane.Detail.MergeAsync(TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Request owner merge")).IsEnabled);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is { } x && x.StartsWith("Merged at head", StringComparison.Ordinal));
    }

    private sealed class SlowClient(InMemoryGitGuiClient inner, Task<GitMergedDto> gate) : IGitGuiClient
    {
        public int Calls { get; private set; }

        public Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default) => inner.ListPrsAsync(provider, repo, state, page, pageSize, cancellationToken);

        public Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) => inner.GetPrAsync(provider, repo, number, cancellationToken);

        public Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default) => inner.GetFilesAsync(provider, repo, number, headSha, cancellationToken);

        public Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) => inner.GetChecksAsync(provider, repo, number, cancellationToken);

        public Task<GitMergedDto> MergeAsync(string provider, string repo, long number, GitMergeRequestDto request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return gate;
        }
    }
}

public sealed class ServerMergeClientTests
{
    private sealed class PostTransport(int status, string body) : IGitHttpTransport
    {
        public string? Path { get; private set; }

        public byte[]? Body { get; private set; }

        public Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default)
        {
            Path = path;
            Body = jsonBody;
            return Task.FromResult(new GitHttpResponse(status, Encoding.UTF8.GetBytes(body)));
        }
    }

    private static GitMergeRequestDto Request() => new(
        Fixtures.Head1, "T (#1)", "Signed-off-by: x",
        new GitOwnerApprovalDto("pr.merge", Guid.NewGuid(), "AA==", Fixtures.Head1, Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), "AA==", "AA=="));

    [Fact]
    public async Task Posts_the_dto_to_the_merge_route_and_returns_the_merged_head()
    {
        var t = new PostTransport(200, $$"""{"headSha":"{{Fixtures.Head1}}"}""");

        var merged = await new ServerGitGuiClient(t).MergeAsync("github", "acme/widgets", 7, Request());

        Assert.Equal(Fixtures.Head1, merged.HeadSha);
        Assert.Equal("git/github/repos/acme/widgets/prs/7/merge", t.Path);
        var sent = JsonSerializer.Deserialize(t.Body!, GitReadContractsJsonContext.Default.GitMergeRequestDto)!;
        Assert.Equal(Fixtures.Head1, sent.HeadSha);
        Assert.Equal("pr.merge", sent.Approval.Action);
    }

    [Fact]
    public async Task Status_409_with_a_sha_is_head_moved()
    {
        var t = new PostTransport(409, $$"""{"currentHeadSha":"{{Fixtures.Head2}}"}""");
        var ex = await Assert.ThrowsAsync<HeadMovedException>(() => new ServerGitGuiClient(t).MergeAsync("github", "acme/widgets", 7, Request()));
        Assert.Equal(Fixtures.Head2, ex.CurrentSha);
    }

    [Fact]
    public async Task Status_403_is_a_generic_refusal_that_keeps_no_detail()
    {
        var t = new PostTransport(403, """{"error":"merge_not_approved"}""");
        var ex = await Assert.ThrowsAsync<GitMergeNotApprovedException>(() => new ServerGitGuiClient(t).MergeAsync("github", "acme/widgets", 7, Request()));
        Assert.DoesNotContain("merge_not_approved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_429_keeps_the_retry_hint()
    {
        var t = new PostTransport(429, """{"error":"rate_limited","retryAfterSeconds":12}""");
        var ex = await Assert.ThrowsAsync<GitRateLimitedException>(() => new ServerGitGuiClient(t).MergeAsync("github", "acme/widgets", 7, Request()));
        Assert.Equal(12, ex.RetryAfterSeconds);
    }

    [Fact]
    public async Task Status_502_is_unavailable_and_404_and_400_are_typed()
    {
        await Assert.ThrowsAsync<GitServerUnavailableException>(() => new ServerGitGuiClient(new PostTransport(502, """{"error":"forge_unavailable"}""")).MergeAsync("github", "acme/widgets", 7, Request()));
        await Assert.ThrowsAsync<GitNotFoundException>(() => new ServerGitGuiClient(new PostTransport(404, """{"error":"not_found"}""")).MergeAsync("github", "acme/widgets", 7, Request()));
        var bad = await Assert.ThrowsAsync<GitRequestException>(() => new ServerGitGuiClient(new PostTransport(400, """{"error":"bad_request"}""")).MergeAsync("github", "acme/widgets", 7, Request()));
        Assert.Equal("bad_request", bad.ErrorCode);
    }

    [Fact]
    public async Task A_connection_failure_is_unavailable()
    {
        var t = new ThrowingTransport();
        await Assert.ThrowsAsync<GitServerUnavailableException>(() => new ServerGitGuiClient(t).MergeAsync("github", "acme/widgets", 7, Request()));
    }

    [Fact]
    public async Task A_bad_repo_name_is_refused_before_any_request()
    {
        var t = new PostTransport(200, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => new ServerGitGuiClient(t).MergeAsync("github", "../etc", 7, Request()));
        Assert.Null(t.Path);
    }

    private sealed class ThrowingTransport : IGitHttpTransport
    {
        public Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default) => throw new HttpRequestException();

        public Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default) => throw new HttpRequestException();
    }
}
