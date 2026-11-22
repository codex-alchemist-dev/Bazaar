using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

public sealed class GitGuiCompositionTests
{
    private static PaneModuleContext Compose() =>
        new PaneModuleContext().WithGitGui(new ServerEndpoint(new Uri("https://bazaar.example/"), "token"));

    [Fact]
    public void Injects_what_the_git_panes_read()
    {
        var context = Compose();

        Assert.IsType<ServerGitGuiClient>(context.Get<IGitGuiClient>());
        Assert.NotNull(context.Get<IGitGuiSettings>());
        Assert.NotNull(context.Get<DiffSurfaceRegistry>());
        var providers = context.Get<IGitProviderUiRegistry>();
        Assert.NotNull(providers);
        Assert.NotNull(providers.Find("github"));
        Assert.NotNull(providers.Find("gitlab"));
    }

    [Fact]
    public void Does_not_inject_the_merge_approver_or_repo_source()
    {
        var context = Compose();

        Assert.Null(context.Get<IOwnerMergeApprover>());
        Assert.Null(context.Get<IOwnerMergeIdentity>());
        Assert.Null(context.Get<IGitRepoSource>());
    }
}
