using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Phone;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.TalentPool.Tests;

public sealed class ApprovalsPaneTests
{
    private static readonly PaneDescriptor _descriptor = new(PaneKinds.Approvals, ApprovalsPaneViewModel.DefaultTitle, string.Empty, System.Text.Json.JsonElement.Parse("{}"));

    private sealed class Source(params ToolGrantCard[] cards) : IPendingToolGrantSource
    {
        public List<ToolGrantCard> Cards { get; } = [.. cards];
        public bool Throws { get; set; }

        public ValueTask<IReadOnlyList<ToolGrantCard>> ListPendingAsync(CancellationToken cancellationToken) =>
            Throws ? throw new InvalidOperationException("offline") : ValueTask.FromResult<IReadOnlyList<ToolGrantCard>>(Cards.ToList());
    }

    private sealed class Biometric : IBiometricPrompt
    {
        public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(BiometricOutcome.Succeeded);
    }

    private sealed class Signer : IDeviceApprovalSigner
    {
        public string DeviceId => "device-1";
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) => ValueTask.FromResult(new byte[64]);
    }

    private sealed class Sender : IToolGrantSender
    {
        public int Sent { get; private set; }
        public ValueTask SendAsync(string templateId, int version, Bazaar.Approvals.OwnerPayload payload, byte[] signature, CancellationToken cancellationToken) { Sent++; return ValueTask.CompletedTask; }
    }

    private static ToolGrantGate Gate(Sender sender) => new(new Biometric(), new Signer(), sender, Guid.NewGuid());

    private static Window Show(ApprovalsPaneViewModel vm)
    {
        var window = new Window { Width = 400, Height = 900, Content = new ApprovalsPaneView(vm) };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    [AvaloniaFact]
    public async Task Pending_tool_grants_show_in_the_approvals_pane_and_approve_sends_once()
    {
        var sender = new Sender();
        var source = new Source(new ToolGrantCard("code-reviewer", 1, "Code Reviewer", ["shell"]));
        var vm = new ApprovalsPaneViewModel(_descriptor, gate: null, source: null, Gate(sender), source);

        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        var window = Show(vm);

        Assert.Single(vm.ToolGrants);
        Assert.False(vm.IsEmpty);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Grant tools to Code Reviewer (version 1)", texts);

        await vm.ToolGrants[0].ApproveAsync();
        Assert.Equal(1, sender.Sent);

        // The same grant still listed by the server does not become a second card.
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.ToolGrants);
    }

    [AvaloniaFact]
    public async Task A_changed_tool_list_replaces_the_card_and_a_gone_grant_is_removed()
    {
        var source = new Source(new ToolGrantCard("code-reviewer", 1, "Code Reviewer", ["shell"]));
        var vm = new ApprovalsPaneViewModel(_descriptor, null, null, Gate(new Sender()), source);
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        var first = vm.ToolGrants[0];

        source.Cards[0] = new ToolGrantCard("code-reviewer", 1, "Code Reviewer", ["shell", "root"]);
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, Assert.Single(vm.ToolGrants));
        Assert.Equal(["shell", "root"], vm.ToolGrants[0].Tools);

        source.Cards.Clear();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(vm.ToolGrants);
    }

    [AvaloniaFact]
    public async Task A_failed_fetch_leaves_the_pane_working()
    {
        var vm = new ApprovalsPaneViewModel(_descriptor, null, null, Gate(new Sender()), new Source { Throws = true });
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(vm.ToolGrants);
    }
}
