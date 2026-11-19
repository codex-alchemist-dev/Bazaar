using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Accessibility;

namespace Bazaar.Client.Core.Tests.AccessibilitySpike;

// Spike (doc 49 section 2, Avalonia automation OPEN item): does an AutomationProperties.Name set on an Avalonia
// control reach its automation peer in headless mode, so a head can check every control's accessible name?
public class AutomationNameSpikeTests
{
    [AvaloniaFact]
    public void Name_set_on_a_button_reaches_its_automation_peer()
    {
        var button = new Button { Content = "Send", [AutomationProperties.NameProperty] = "Send message" };
        var window = new Window { Content = button };
        window.Show();

        var peer = ControlAutomationPeer.CreatePeerForElement(button);

        Assert.Equal("Send message", peer.GetName());
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.True(AccessibleName.IsValid(peer.GetName()));
    }

    [AvaloniaFact]
    public void Control_without_an_explicit_name_is_caught_by_the_name_check()
    {
        var button = new Button { Content = "Send" };
        var window = new Window { Content = button };
        window.Show();

        // Finding: the peer's computed name falls back to the visible text ("Send"), so the check must read the
        // explicit AutomationProperties.Name, not the peer name, or unnamed controls would pass.
        Assert.NotNull(AccessibleName.Problem(AutomationProperties.GetName(button)));
        Assert.Equal("Send", ControlAutomationPeer.CreatePeerForElement(button).GetName());
    }
}
