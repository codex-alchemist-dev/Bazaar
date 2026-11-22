using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// A short notice: one wrapped line (up to date, update ready after restart, or why the check failed) and an OK button.
/// Pure view; <see cref="UpdateCheckCommand"/> decides when to show it.
/// </summary>
public sealed class UpdateNoticeView : StackPanel
{
    private readonly TextBlock _text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>Creates the view for <paramref name="notice"/>. <paramref name="onClose"/> runs when OK is pressed.</summary>
    public UpdateNoticeView(UpdateNotice notice, Action? onClose = null)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Spacing = 12;
        Margin = new Thickness(16);
        _text.Text = notice.Text;
        Notice = notice;

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => onClose?.Invoke();
        Children.Add(_text);
        Children.Add(ok);
    }

    /// <summary>The notice shown.</summary>
    public UpdateNotice Notice { get; }

    /// <summary>The text on screen.</summary>
    public string? DisplayedText => _text.Text;
}
