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

    [AvaloniaFact]
    public void Typing_a_code_into_the_text_box_enables_pair()
    {
        // Regression: the code is typed into the TextBox, and the button must follow without losing focus (UpdateSourceTrigger).
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), new GatedPairingHandler()), "android");
        var window = new Window { Content = new PairingView(model) };
        window.Show();

        var pair = window.GetLogicalDescendants().OfType<Button>().Single();
        var code = window.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Code from your other device");
        Assert.False(pair.IsEnabled);

        code.Text = "ABCD2345";

        Assert.Equal("ABCD2345", model.UserCode);
        Assert.True(pair.IsEnabled);
    }
}
