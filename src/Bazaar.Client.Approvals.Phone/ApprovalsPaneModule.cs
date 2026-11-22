using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Approvals.Phone;

/// <summary>
/// Registers the phone approvals pane (<see cref="PaneKinds.Approvals"/>, doc 29 Approvals). The head supplies the
/// <see cref="ApprovalGate"/> (biometric, device key, sender) and the <see cref="IPendingApprovalSource"/> through
/// <see cref="PaneModuleContext.With{T}"/>. With either missing the pane renders and says what is missing; it decides nothing.
/// </summary>
public sealed class ApprovalsPaneModule : IPaneModule
{
    private static readonly JsonElement _emptyModel = JsonElement.Parse("{}");

    public string Id => PaneKinds.Approvals;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        registry.Register(PaneKinds.Approvals, descriptor => new ApprovalsPaneViewModel(descriptor, context.Get<ApprovalGate>(), context.Get<IPendingApprovalSource>()));
        registry.RegisterView<ApprovalsPaneViewModel>(pane =>
        {
            _ = pane.RefreshAsync();
            return new ApprovalsPaneView(pane);
        });
    }

    /// <summary>The phone body of the approvals pane, or null for any other kind. A head passes it to <c>FirstPartyPanes.PhonePaneBodies</c>.</summary>
    public static Control? PhoneBody(string kind, PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return kind == PaneKinds.Approvals
            ? registry.CreateView(registry.Create(new PaneDescriptor(kind, ApprovalsPaneViewModel.DefaultTitle, string.Empty, _emptyModel)))
            : null;
    }
}
