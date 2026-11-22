using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bazaar.Client.Linux;

/// <summary>A small window that shows the honest text a palette command produced, such as the default-app status.</summary>
internal static class StatusNotice
{
    /// <summary>Builds the notice. Separate from <see cref="Show"/> so a test can read it without a visible owner.</summary>
    public static Window Create(string title, string text)
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }

    /// <summary>Shows the notice over <paramref name="owner"/> when it is visible, else on its own. Call on the UI thread.</summary>
    public static void Show(Window owner, string title, string text)
    {
        var window = Create(title, text);
        if (owner.IsVisible)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
