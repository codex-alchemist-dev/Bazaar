using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Vault;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Android.Gate;

/// <summary>
/// Builds the phone shell the Android head shows at startup, on a fake company endpoint that never touches the network.
/// The head builds it from <c>ClientServer.Current</c>; the gate passes the fake endpoint in directly, so it does not change
/// the global client endpoint. Nothing here is compiled into the head.
/// </summary>
internal static class GatePhoneShell
{
    /// <summary>The endpoint the fake reports as paired. The host never resolves, and no request is sent to it.</summary>
    public static readonly ServerEndpoint PairedEndpoint = new(new Uri("http://gate.invalid/"), "gate-fake-credential");

    /// <summary>A phone shell with the Client.Core panes, a Constellation source and a Vault API that return nothing.</summary>
    public static PhoneShell Create()
    {
        var tiles = new EmptyTileSource();
        var vault = VaultApiFactory.Create(PairedEndpoint, new EmptyResponseHandler());
        // The Dashboard, then Constellation, then the GC pane, then Vault, as in the Android head (pager order).
        IPaneModule[] modules =
        [
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            new EnginePaneModule(),
        ];
        var features = new PaneModuleContext().With<IConstellationTileSource>(tiles).With(vault);
        var registry = FirstPartyPanes.CreateRegistry(modules, features: features);
        Func<string, Control?> production = FirstPartyPanes.PhonePaneBodies(registry, extra: kind => GcPaneModule.PhoneBody(kind, registry));

        // The production pager shows a kind-name placeholder for the Vault search pane, so the shell has no interactive
        // control and the scan would pass vacuously. The gate shows that pane's real view (query box and search button) so
        // the scan has controls to check. Production is unchanged; only this gate builds the view.
        return new PhoneShell(registry, kind => kind == PaneKinds.VaultSearch
            ? registry.CreateView(registry.Create(new PaneDescriptor(kind, "Vault search", string.Empty, VaultModel)))
            : production(kind));
    }

    /// <summary>A Vault pane model naming a placeholder vault. The view makes no request until someone searches.</summary>
    private static readonly JsonElement VaultModel = JsonElement.Parse("""{"vault":"00000000-0000-0000-0000-000000000000"}""");
}

/// <summary>A tile source with no tiles. Every frame request answers "no frame", so the Constellation pane shows its empty state.</summary>
internal sealed class EmptyTileSource : IConstellationTileSource
{
    public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<TileFrame?>(null);
}

/// <summary>An HTTP handler that answers every request in memory with 404, so no socket is opened.</summary>
internal sealed class EmptyResponseHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
}

/// <summary>The scan result for one view of the gate.</summary>
/// <param name="View">The view's name in the log, for example <c>pairing</c>.</param>
/// <param name="Interactive">How many interactive controls the scan saw. Zero means the scan would pass vacuously.</param>
/// <param name="Problems">The controls that failed the accessible-name check.</param>
internal sealed record GateViewScan(string View, int Interactive, IReadOnlyList<AccessibleControlProblem> Problems)
{
    /// <summary>Scans <paramref name="root"/> and counts its interactive controls, so an empty view fails rather than passing.</summary>
    public static GateViewScan Of(string view, Control root)
    {
        int interactive = root.GetSelfAndLogicalDescendants().OfType<Control>()
            .Count(c => AccessibleControlScan.InteractiveTypes.Any(t => t.IsInstanceOfType(c)));
        return new GateViewScan(view, interactive, AccessibleControlScan.Find(root));
    }

    /// <summary>True when the view has at least one interactive control and every one has an accessible name.</summary>
    public bool Passed => Interactive > 0 && Problems.Count == 0;
}
