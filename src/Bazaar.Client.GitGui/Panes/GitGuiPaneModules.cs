using Bazaar.Client.Core.Panes;
using Bazaar.Client.GitGui.ViewModels;

namespace Bazaar.Client.GitGui.Panes;

/// <summary>Registers pane <c>git.view</c>. Reads <see cref="IGitRepoSource"/> from the context; without one the pane says so.</summary>
public sealed class GitViewPaneModule : IPaneModule
{
    public string Id => GitGuiPaneKinds.View;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var source = context.Get<IGitRepoSource>();
        registry.Register(GitGuiPaneKinds.View, descriptor => new GitViewPaneViewModel(descriptor, source is null ? null : new GitViewViewModel(source)));
        registry.RegisterView<GitViewPaneViewModel>(GitGuiPaneViews.View);
    }
}

/// <summary>Registers pane <c>git.prs</c>.</summary>
public sealed class GitPrsPaneModule : IPaneModule
{
    public string Id => GitGuiPaneKinds.Prs;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var services = GitGuiServices.From(context);
        registry.Register(GitGuiPaneKinds.Prs, descriptor => new GitPrsPaneViewModel(descriptor, services));
        registry.RegisterView<GitPrsPaneViewModel>(GitGuiPaneViews.Prs);
    }
}

/// <summary>Registers pane <c>git.pr</c>.</summary>
public sealed class GitPrPaneModule : IPaneModule
{
    public string Id => GitGuiPaneKinds.Pr;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var services = GitGuiServices.From(context);
        registry.Register(GitGuiPaneKinds.Pr, descriptor => new GitPrPaneViewModel(descriptor, services));
        registry.RegisterView<GitPrPaneViewModel>(GitGuiPaneViews.Pr);
    }
}

/// <summary>Registers pane <c>git.diff</c>.</summary>
public sealed class GitDiffPaneModule : IPaneModule
{
    public string Id => GitGuiPaneKinds.Diff;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var services = GitGuiServices.From(context);
        registry.Register(GitGuiPaneKinds.Diff, descriptor => new GitDiffPaneViewModel(descriptor, services));
        registry.RegisterView<GitDiffPaneViewModel>(GitGuiPaneViews.DiffPane);
    }
}
