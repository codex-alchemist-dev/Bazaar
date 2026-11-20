using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Linux.Updates;

/// <summary>A one-line notice strip that shows the coordinator's notice and hides when there is none.</summary>
public sealed class UpdateNoticeView : TextBlock
{
    public UpdateNoticeView(UpdateCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        TextWrapping = TextWrapping.Wrap;
        Margin = new Thickness(8, 4);
        Show(coordinator.Notice);
        coordinator.NoticeChanged += Show;
    }

    private void Show(string? notice)
    {
        Text = notice ?? string.Empty;
        IsVisible = !string.IsNullOrEmpty(notice);
    }
}
