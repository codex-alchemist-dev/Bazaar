using Avalonia.Automation;
using Avalonia.Controls;

namespace Bazaar.Ex.Head;

/// <summary>
/// Speaks mnemonic feedback through a live region: a visually hidden text block whose text changes. Screen readers
/// announce the change, politely or assertively as <see cref="AnnouncePriority"/> asks. Place <see cref="Control"/> in
/// the window once, anywhere in the tree.
/// </summary>
public sealed class MnemonicLiveRegion : IMnemonicAnnouncer
{
    public MnemonicLiveRegion()
    {
        Control = new TextBlock { Width = 1, Height = 1, Opacity = 0 };
    }

    public TextBlock Control { get; }

    public void Announce(string text, AnnouncePriority priority)
    {
        ArgumentNullException.ThrowIfNull(text);
        AutomationProperties.SetLiveSetting(
            Control,
            priority == AnnouncePriority.Assertive ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Control.Text = text;
    }
}
