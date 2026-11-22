using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Linux.Updates;

namespace Bazaar.Client.Linux.Tests;

public class UpdateNoticeViewHeadlessTests
{
    [AvaloniaFact]
    public void Notice_view_shows_the_text_and_closes_on_ok()
    {
        var notice = new UpdateNotice(UpdateNoticeKind.ReadyAfterRestart, "Update 0.2.0 is ready. It applies the next time you restart Bazaar.");
        var closed = false;
        var view = new UpdateNoticeView(notice, () => closed = true);
        var window = new Window { Content = view };

        window.Show();

        Assert.Equal(notice.Text, view.DisplayedText);
        var ok = Assert.IsType<Button>(view.Children[1]);
        ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed);
        window.Close();
    }

    [Fact]
    public void Check_command_is_a_local_palette_entry()
    {
        Assert.Equal(UpdateCoordinator.CheckCommandId, UpdateCheckCommand.Command.Id);
        Assert.Equal(Bazaar.Client.Commands.CommandSource.Local, UpdateCheckCommand.Command.Source);
    }
}
