using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Community.Avalonia;

/// <summary>The community-drafts pane's view model: the heading and the parsed review list.</summary>
public sealed class DraftReviewPaneViewModel : IPaneViewModel
{
    public DraftReviewPaneViewModel(PaneDescriptor descriptor, DraftReviewViewModel review)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Review = review ?? throw new ArgumentNullException(nameof(review));
    }

    public string Kind { get; }

    public string Title { get; }

    public DraftReviewViewModel Review { get; }
}

/// <summary>
/// Registers the community draft-review pane (<see cref="PaneKinds.CommunityDrafts"/>, doc 36 work package 8). The head
/// supplies the device-key signer and the decision sender, through the constructor or <see cref="PaneModuleContext.With{T}"/>
/// (<see cref="IDecisionSigner"/>, <see cref="IDraftReviewDecisionSender"/>, optionally <see cref="TimeProvider"/>). With no
/// signer or sender the pane still renders, and Approve, Deny and Edit report that signing is not available.
/// </summary>
public sealed class CommunityDraftsPaneModule : IPaneModule
{
    private readonly IDecisionSigner? _signer;
    private readonly IDraftReviewDecisionSender? _sender;
    private readonly TimeProvider? _time;

    public CommunityDraftsPaneModule(
        IDecisionSigner? signer = null,
        IDraftReviewDecisionSender? sender = null,
        TimeProvider? time = null)
    {
        _signer = signer;
        _sender = sender;
        _time = time;
    }

    public string Id => PaneKinds.CommunityDrafts;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var signer = _signer ?? context.Get<IDecisionSigner>() ?? new UnavailableSigner();
        var sender = _sender ?? context.Get<IDraftReviewDecisionSender>() ?? new UnavailableSender();
        var time = _time ?? context.Get<TimeProvider>();

        registry.Register(PaneKinds.CommunityDrafts, descriptor =>
        {
            try
            {
                return new DraftReviewPaneViewModel(descriptor, DraftReviewViewModel.FromDescriptor(descriptor, signer, sender, time));
            }
            catch (FormatException ex)
            {
                throw new PaneModelException(ex.Message);
            }
        });
        registry.RegisterView<DraftReviewPaneViewModel>(pane => new DraftReviewView(pane.Review));
    }

    private sealed class UnavailableSigner : IDecisionSigner
    {
        public Task<Bazaar.Contracts.Approvals.ApprovalDecisionDto> SignAsync(
            Bazaar.Contracts.Approvals.ApprovalCardDto card,
            Bazaar.Contracts.Approvals.ApprovalDecisionKind decision,
            DateTimeOffset decidedAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No device signing key is available in this client.");
    }

    private sealed class UnavailableSender : IDraftReviewDecisionSender
    {
        public Task SendAsync(Bazaar.Contracts.Community.DraftReviewDecisionDto decision, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No server connection is available to send the decision.");
    }
}
