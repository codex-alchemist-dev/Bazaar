using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Community.Avalonia;

/// <summary>The community-log pane's view model: the heading and the log list.</summary>
public sealed class CommunityLogPaneViewModel : IPaneViewModel
{
    public CommunityLogPaneViewModel(PaneDescriptor descriptor, CommunityLogViewModel log)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Kind { get; }

    public string Title { get; }

    public CommunityLogViewModel Log { get; }
}

/// <summary>
/// Registers the community log pane (<see cref="PaneKinds.CommunityLog"/>, doc 36 work package 9). The head supplies an
/// <see cref="ICommunityLogSource"/> through the constructor or <see cref="PaneModuleContext.With{T}"/>. With none, the pane
/// still renders and shows an error state. The descriptor's model is not used: the pane loads its own pages.
/// </summary>
public sealed class CommunityLogPaneModule : IPaneModule
{
    private readonly ICommunityLogSource? _source;

    public CommunityLogPaneModule(ICommunityLogSource? source = null) => _source = source;

    public string Id => PaneKinds.CommunityLog;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var source = _source ?? context.Get<ICommunityLogSource>() ?? new UnavailableSource();

        registry.Register(PaneKinds.CommunityLog, descriptor =>
        {
            var log = new CommunityLogViewModel(source);
            _ = log.RefreshAsync(); // Failures land in the view model's error state.
            return new CommunityLogPaneViewModel(descriptor, log);
        });
        registry.RegisterView<CommunityLogPaneViewModel>(pane => new CommunityLogView(pane.Log));
    }

    private sealed class UnavailableSource : ICommunityLogSource
    {
        public Task<Bazaar.Contracts.Community.CommunityLogPageDto> LoadAsync(
            Bazaar.Contracts.Community.CommunityLogQueryDto query,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No server connection is available to read the community log.");
    }
}
