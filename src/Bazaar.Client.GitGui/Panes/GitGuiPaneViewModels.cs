using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.ViewModels;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Git;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitGui.Panes;

/// <summary>What the pane factories read from the head: the services it injected, with the shipped defaults for the optional ones.</summary>
internal sealed record GitGuiServices(
    IGitGuiClient Client,
    IGitProviderUiRegistry Providers,
    IGitRepoSource? Repos,
    IGitGuiSettings Settings,
    DiffSurfaceRegistry Surfaces)
{
    internal static GitGuiServices From(PaneModuleContext context) => new(
        context.Get<IGitGuiClient>() ?? new DisconnectedGitGuiClient(),
        context.Get<IGitProviderUiRegistry>() ?? new GitProviderUiRegistry([]),
        context.Get<IGitRepoSource>(),
        context.Get<IGitGuiSettings>() ?? GitGuiSettings.Default,
        context.Get<DiffSurfaceRegistry>() ?? new DiffSurfaceRegistry());
}

/// <summary>Stands in when a head injected no <see cref="IGitGuiClient"/>: every read fails with a message the pane shows.</summary>
internal sealed class DisconnectedGitGuiClient : IGitGuiClient
{
    private static InvalidOperationException Fail() => new("Git is not connected to a server.");

    public Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default) => throw Fail();

    public Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) => throw Fail();

    public Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default) => throw Fail();

    public Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default) => throw Fail();
}

/// <summary>Pane <c>git.view</c>: repos, branches and recent commits.</summary>
public sealed class GitViewPaneViewModel : IPaneViewModel
{
    internal GitViewPaneViewModel(PaneDescriptor descriptor, GitViewViewModel? view)
    {
        Kind = descriptor.Kind;
        Title = "Git";
        View = view;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>Null when the head injected no <see cref="IGitRepoSource"/>; the pane then says so.</summary>
    public GitViewViewModel? View { get; }
}

/// <summary>Pane <c>git.prs</c>: the PR list of one repo.</summary>
public sealed class GitPrsPaneViewModel : IPaneViewModel
{
    internal GitPrsPaneViewModel(PaneDescriptor descriptor, GitGuiServices services)
    {
        Kind = descriptor.Kind;
        var (provider, repo) = GitGuiModel.ReadRepo(descriptor);
        List = new PrListViewModel(services.Client, provider, repo, services.Settings.PrPageSize, services.Providers.Find(provider));
        Title = List.Title;
    }

    public string Kind { get; }

    public string Title { get; }

    public PrListViewModel List { get; }
}

/// <summary>Pane <c>git.pr</c>: one PR with files, checks, reviews and the merge card.</summary>
public sealed class GitPrPaneViewModel : IPaneViewModel
{
    internal GitPrPaneViewModel(PaneDescriptor descriptor, GitGuiServices services)
    {
        Kind = descriptor.Kind;
        var (provider, repo) = GitGuiModel.ReadRepo(descriptor);
        Detail = new PrDetailViewModel(
            services.Client, provider, repo, GitGuiModel.ReadNumber(descriptor), services.Providers.Find(provider), services.Settings.DiffContextLines);
        Title = Detail.Title;
        Surfaces = services.Surfaces;
    }

    public string Kind { get; }

    public string Title { get; }

    public PrDetailViewModel Detail { get; }

    public DiffSurfaceRegistry Surfaces { get; }
}

/// <summary>Pane <c>git.diff</c>: the diff of one file of a PR.</summary>
public sealed class GitDiffPaneViewModel : IPaneViewModel
{
    internal GitDiffPaneViewModel(PaneDescriptor descriptor, GitGuiServices services)
    {
        Kind = descriptor.Kind;
        var (provider, repo) = GitGuiModel.ReadRepo(descriptor);
        Detail = new PrDetailViewModel(
            services.Client, provider, repo, GitGuiModel.ReadNumber(descriptor), services.Providers.Find(provider), services.Settings.DiffContextLines);
        Path = descriptor.Model.TryGetProperty("path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String ? path.GetString() : null;
        Title = $"Diff · {Detail.Title}";
        Surfaces = services.Surfaces;
    }

    public string Kind { get; }

    public string Title { get; }

    public PrDetailViewModel Detail { get; }

    /// <summary>The file path from the model, or null to show the first file.</summary>
    public string? Path { get; }

    public DiffSurfaceRegistry Surfaces { get; }

    /// <summary>Loads the PR and selects the model's file, or the first file.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await Detail.LoadAsync(cancellationToken).ConfigureAwait(true);
        var path = Path ?? (Detail.Files.Count > 0 ? Detail.Files[0].Path : null);
        if (path is not null)
        {
            Detail.SelectFile(path);
        }
    }
}

internal static class GitGuiModel
{
    internal static (string Provider, string Repo) ReadRepo(PaneDescriptor descriptor)
    {
        RequireObject(descriptor);
        return (PaneValues.RequiredString(descriptor.Model, "provider"), PaneValues.RequiredString(descriptor.Model, "repo"));
    }

    internal static long ReadNumber(PaneDescriptor descriptor)
    {
        RequireObject(descriptor);
        return PaneValues.RequiredInt(descriptor.Model, "number");
    }

    private static void RequireObject(PaneDescriptor descriptor)
    {
        if (descriptor.Model.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new PaneModelException("the pane model must be an object");
        }
    }
}
