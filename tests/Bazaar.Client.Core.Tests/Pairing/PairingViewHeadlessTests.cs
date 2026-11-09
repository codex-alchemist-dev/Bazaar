using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Pairing;
using static Bazaar.Client.Core.Tests.Pairing.PairingFlowTests;

namespace Bazaar.Client.Core.Tests.Pairing;

public class PairingViewHeadlessTests
{
    [AvaloniaFact]
    public void Pair_button_and_message_follow_the_view_model()
    {
        // The gated handler is never released, so the attempt stays in flight for the whole test and no continuation runs after it.
        var handler = new GatedPairingHandler();
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows");
        var window = new Window { Content = new PairingView(model) };
        window.Show();

        var pair = window.GetLogicalDescendants().OfType<Button>().Single();
        var message = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == model.Message);
        Assert.False(pair.IsEnabled);

        model.UserCode = "ABCD2345";
        Assert.True(pair.IsEnabled);

        _ = model.PairAsync();
        Assert.False(pair.IsEnabled);
        Assert.Equal("Waiting for approval on your other device.", message.Text);
    }
}
