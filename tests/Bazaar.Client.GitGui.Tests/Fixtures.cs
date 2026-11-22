using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Git;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitGui.Tests;

internal static class Fixtures
{
    internal const string Head1 = "1111111111111111111111111111111111111111";
    internal const string Head2 = "2222222222222222222222222222222222222222";

    internal static GitPrSummaryDto Summary(long number, string state = "open", bool draft = false, string head = Head1, long updated = 0) =>
        new("github", "acme/widgets", number, $"PR {number}", "dev", state, draft, head, "feature", "main", updated == 0 ? number : updated);

    internal static GitPrDetailDto Detail(GitPrSummaryDto summary) =>
        new(summary, "Body text", 2, 3, 1, [new GitReviewDto("rev", "approve", summary.HeadSha, 1)]);

    internal static GitCheckDto Check(string state, string name = "ci", string head = Head1) => new(name, state, head, null);

    internal static readonly GitPrFileDto[] Files =
    [
        new("src/a.cs", "modified", 2, 1, "@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n+extra\n three\n", false),
        new("src/b.cs", "added", 1, 0, "@@ -0,0 +1 @@\n+only\n", false),
    ];

    internal static InMemoryGitGuiClient ClientWith(int prs, int pageable = 0)
    {
        var client = new InMemoryGitGuiClient();
        for (var i = 1; i <= prs + pageable; i++)
        {
            client.Set(Detail(Summary(i)), Files, [Check("success")]);
        }

        return client;
    }

    internal static PaneDescriptor Descriptor(string kind, string json) =>
        new(kind, kind, string.Empty, JsonElement.Parse(json));

    internal static PaneRegistry Registry(IGitGuiClient client, IGitRepoSource? repos = null, params IGitProviderUi[] providers) =>
        Registry(client, null, repos, providers);

    internal static PaneRegistry Registry(IGitGuiClient client, IOwnerMergeApprover? approver, IGitRepoSource? repos, params IGitProviderUi[] providers)
    {
        var context = new PaneModuleContext()
            .With(client)
            .With<IGitProviderUiRegistry>(new GitProviderUiRegistry(providers));
        if (repos is not null)
        {
            context = context.With(repos);
        }

        if (approver is not null)
        {
            context = context.With(approver);
        }

        return PaneRegistry.Compose(new PlainViews(), GitGuiPanes.Modules, context);
    }
}

internal sealed class FakeProviderUi(string id = "github", string noun = "pull request") : IGitProviderUi
{
    public string ProviderId { get; } = id;

    public string DisplayName => ProviderId;

    public string PrNoun { get; } = noun;

    public string CheckLabel(string state) => $"check:{state}";

    public string ReviewLabel(string verdict) => $"review:{verdict}";

    public Uri? PrWebUrl(string repoFullName, long number) => null;

    public Uri? CommitWebUrl(string repoFullName, string sha) => null;
}

internal sealed class FakeRepoSource : IGitRepoSource
{
    public Task<IReadOnlyList<GitRepoRow>> ListReposAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitRepoRow>>([new GitRepoRow("github", "acme/widgets", "main")]);

    public Task<IReadOnlyList<GitBranchRow>> ListBranchesAsync(GitRepoRow repo, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitBranchRow>>([new GitBranchRow("main", Fixtures.Head1), new GitBranchRow("feature", Fixtures.Head2)]);

    public Task<IReadOnlyList<GitCommitRow>> ListCommitsAsync(GitRepoRow repo, string branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommitRow>>([new GitCommitRow(Fixtures.Head1, "first commit", "dev", 1)]);
}

/// <summary>A plain view factory: the title as a text block above the body. The Client.Core factory is not referenced here.</summary>
internal sealed class PlainViews : IPaneViewFactory
{
    public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

    public Control WithTitle(string title, Control body) =>
        new StackPanel { Children = { new TextBlock { Text = title }, body } };
}
